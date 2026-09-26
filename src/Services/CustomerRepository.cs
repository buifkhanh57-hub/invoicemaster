using System;
using System.Collections.Generic;
using System.Linq;
using InvoiceMaster.Models;

namespace InvoiceMaster.Services
{
    /// <summary>
    /// Repository over <c>customers.json</c>. Handles identifier sequencing, uniqueness
    /// checks, validation and persistence. Mutating methods persist atomically on success.
    /// </summary>
    public sealed class CustomerRepository
    {
        private readonly JsonStore<List<Customer>> _store;
        private readonly object _sync = new();
        private List<Customer> _customers;

        /// <summary>
        /// Creates the repository and loads existing customers from the store.
        /// </summary>
        /// <param name="store">JSON store pointing at customers.json.</param>
        public CustomerRepository(JsonStore<List<Customer>> store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _customers = store.Load() ?? new List<Customer>();
        }

        /// <summary>Path of the underlying customers.json file.</summary>
        public string FilePath => _store.FilePath;

        /// <summary>Number of customers currently stored.</summary>
        public int Count
        {
            get
            {
                lock (_sync)
                {
                    return _customers.Count;
                }
            }
        }

        /// <summary>Returns all customers sorted by identifier.</summary>
        /// <returns>Snapshot list safe to iterate while the store changes.</returns>
        public IReadOnlyList<Customer> GetAll()
        {
            lock (_sync)
            {
                return _customers
                    .OrderBy(c => c.Id, StringComparer.Ordinal)
                    .ToList();
            }
        }

        /// <summary>Finds a customer by exact identifier (case-insensitive).</summary>
        /// <param name="id">Identifier such as "CUS-0001".</param>
        /// <returns>The customer, or null when not found.</returns>
        public Customer? FindById(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            string needle = id.Trim();
            lock (_sync)
            {
                return _customers.FirstOrDefault(c => string.Equals(c.Id, needle, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>Finds a customer by exact identifier or throws.</summary>
        /// <param name="id">Identifier such as "CUS-0001".</param>
        /// <returns>The matching customer.</returns>
        public Customer RequireById(string? id)
        {
            Customer? found = FindById(id);
            if (found is null)
            {
                throw NotFoundException.For(
                    "customer",
                    (id ?? string.Empty).Trim(),
                    "Run 'invoicemaster customer list' to see existing customers.");
            }

            return found;
        }

        /// <summary>Keyword search across name, email, tax id, notes and id.</summary>
        /// <param name="term">Raw user term; blank matches everything.</param>
        /// <returns>Matching customers sorted by identifier.</returns>
        public IReadOnlyList<Customer> Search(string? term)
        {
            lock (_sync)
            {
                return _customers
                    .Where(c => c.MatchesSearch(term))
                    .OrderBy(c => c.Id, StringComparer.Ordinal)
                    .ToList();
            }
        }

        /// <summary>Computes the next free customer identifier.</summary>
        /// <returns>Identifier such as "CUS-0007".</returns>
        public string NextId()
        {
            lock (_sync)
            {
                int max = 0;
                foreach (Customer customer in _customers)
                {
                    if (Customer.IdPattern.IsMatch(customer.Id)
                        && int.TryParse(customer.Id.AsSpan(4), out int sequence)
                        && sequence > max)
                    {
                        max = sequence;
                    }
                }

                return Customer.FormatId(max + 1);
            }
        }

        /// <summary>
        /// Validates and stores a new customer, assigning id and timestamps when blank.
        /// </summary>
        /// <param name="customer">Customer to add (id may be blank for auto-assignment).</param>
        /// <returns>The stored customer.</returns>
        public Customer Add(Customer customer)
        {
            if (customer is null)
            {
                throw new ArgumentNullException(nameof(customer));
            }

            lock (_sync)
            {
                var errors = new List<string>(customer.Validate());
                if (errors.Count > 0)
                {
                    throw ValidationException.ForErrors("The customer cannot be added:", errors);
                }

                customer.Name = customer.Name.Trim();
                customer.Email = customer.Email.Trim();
                customer.Phone = customer.Phone.Trim();
                customer.TaxId = customer.TaxId.Trim();
                customer.Notes = customer.Notes.Trim();
                if (string.IsNullOrWhiteSpace(customer.Id))
                {
                    customer.Id = NextId();
                }
                else if (FindById(customer.Id) is not null)
                {
                    throw new ConflictException("Customer id '" + customer.Id + "' already exists.");
                }

                if (!string.IsNullOrWhiteSpace(customer.Email))
                {
                    Customer? clash = _customers.FirstOrDefault(c =>
                        string.Equals(c.Email, customer.Email.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (clash is not null)
                    {
                        throw new ConflictException("Email '" + customer.Email + "' is already used by customer " + clash.Id + ".");
                    }
                }

                DateTime now = DateTime.UtcNow;
                customer.CreatedAtUtc = now;
                customer.UpdatedAtUtc = now;
                _customers.Add(customer);
                Persist();
                return customer;
            }
        }

        /// <summary>
        /// Applies a mutation to a working copy, validates it, then swaps it in and
        /// persists. A failing mutation leaves the stored customer untouched.
        /// </summary>
        /// <param name="id">Identifier of the customer to update.</param>
        /// <param name="mutate">Action editing the working copy.</param>
        /// <returns>The updated customer.</returns>
        public Customer Update(string id, Action<Customer> mutate)
        {
            if (mutate is null)
            {
                throw new ArgumentNullException(nameof(mutate));
            }

            lock (_sync)
            {
                Customer current = RequireById(id);
                Customer draft = current.Clone();
                mutate(draft);

                var errors = new List<string>(draft.Validate());
                if (!string.IsNullOrWhiteSpace(draft.Email))
                {
                    Customer? clash = _customers.FirstOrDefault(c =>
                        string.Equals(c.Email, draft.Email.Trim(), StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(c.Id, draft.Id, StringComparison.OrdinalIgnoreCase));
                    if (clash is not null)
                    {
                        errors.Add("Email '" + draft.Email + "' is already used by customer " + clash.Id + ".");
                    }
                }

                if (errors.Count > 0)
                {
                    throw ValidationException.ForErrors("Customer " + current.Id + " cannot be updated:", errors);
                }

                draft.Touch();
                int index = _customers.FindIndex(c => string.Equals(c.Id, current.Id, StringComparison.OrdinalIgnoreCase));
                _customers[index] = draft;
                Persist();
                return draft;
            }
        }

        /// <summary>
        /// Removes a customer permanently. Callers must ensure no invoices reference it.
        /// </summary>
        /// <param name="id">Identifier of the customer to delete.</param>
        public void Delete(string id)
        {
            lock (_sync)
            {
                Customer current = RequireById(id);
                _customers.Remove(current);
                Persist();
            }
        }

        private void Persist()
        {
            _store.Save(_customers);
        }
    }
}
