// csharp-design-patterns — four classic GoF patterns with runnable demos.
// Build & run: dotnet run

using System;
using System.Collections.Generic;

namespace DesignPatterns
{
    // ---------------------------------------------------------- Singleton --
    public sealed class AppConfig
    {
        private static AppConfig? _instance;
        private static readonly object Lock = new();

        public Dictionary<string, string> Settings { get; } = new();

        private AppConfig()
        {
            Settings["app.name"] = "csharp-design-patterns";
            Settings["app.version"] = "1.0.0";
        }

        public static AppConfig Instance
        {
            get
            {
                if (_instance is null)
                {
                    lock (Lock)
                    {
                        _instance ??= new AppConfig();
                    }
                }
                return _instance;
            }
        }
    }

    // ------------------------------------------------------------ Factory --
    public abstract class Shape
    {
        public abstract double Area();
        public abstract string Name();
    }

    public class Circle : Shape
    {
        private readonly double _radius;
        public Circle(double radius) => _radius = radius;
        public override double Area() => Math.PI * _radius * _radius;
        public override string Name() => $"Circle(r={_radius})";
    }

    public class Rectangle : Shape
    {
        private readonly double _w, _h;
        public Rectangle(double w, double h) { _w = w; _h = h; }
        public override double Area() => _w * _h;
        public override string Name() => $"Rectangle({_w}x{_h})";
    }

    public static class ShapeFactory
    {
        public static Shape Create(string kind, params double[] dims) => kind switch
        {
            "circle" => new Circle(dims[0]),
            "rect" => new Rectangle(dims[0], dims[1]),
            _ => throw new ArgumentException($"unknown shape kind: {kind}")
        };
    }

    // ----------------------------------------------------------- Strategy --
    public interface ISortStrategy
    {
        void Sort(List<int> data);
    }

    public class QuickSortStrategy : ISortStrategy
    {
        public void Sort(List<int> data) => data.Sort(); // in .NET, Array.Sort is introsort
    }

    public class SelectionSortStrategy : ISortStrategy
    {
        public void Sort(List<int> data)
        {
            for (int i = 0; i < data.Count - 1; i++)
            {
                int min = i;
                for (int j = i + 1; j < data.Count; j++)
                    if (data[j] < data[min]) min = j;
                (data[i], data[min]) = (data[min], data[i]);
            }
        }
    }

    public class Sorter
    {
        private ISortStrategy _strategy;
        public Sorter(ISortStrategy strategy) => _strategy = strategy;
        public void SetStrategy(ISortStrategy strategy) => _strategy = strategy;
        public void Sort(List<int> data) => _strategy.Sort(data);
    }

    // ----------------------------------------------------------- Observer --
    public interface IObserver<T> { void OnNext(T item); }

    public class NewsFeed
    {
        private readonly List<IObserver<string>> _subs = new();
        public void Subscribe(IObserver<string> sub) => _subs.Add(sub);
        public void Publish(string headline)
        {
            foreach (var sub in _subs) sub.OnNext(headline);
        }
    }

    public class Reader : IObserver<string>
    {
        private readonly string _name;
        public Reader(string name) => _name = name;
        public void OnNext(string item) =>
            Console.WriteLine($"  [{_name} received] {item}");
    }

    // --------------------------------------------------------------- Main --
    public static class Program
    {
        public static void Main()
        {
            Console.WriteLine("== Singleton ==");
            var cfg = AppConfig.Instance;
            Console.WriteLine($"  {cfg.Settings["app.name"]} v{cfg.Settings["app.version"]}");
            Console.WriteLine($"  same instance? {ReferenceEquals(cfg, AppConfig.Instance)}");

            Console.WriteLine("== Factory ==");
            foreach (var shape in new[] { ShapeFactory.Create("circle", 2.0), ShapeFactory.Create("rect", 3, 4) })
                Console.WriteLine($"  {shape.Name()} area = {shape.Area():F2}");

            Console.WriteLine("== Strategy ==");
            var data = new List<int> { 5, 2, 9, 1, 7 };
            var sorter = new Sorter(new SelectionSortStrategy());
            sorter.Sort(data);
            Console.WriteLine($"  selection-sorted: {string.Join(", ", data)}");
            sorter.SetStrategy(new QuickSortStrategy());
            var more = new List<int> { 100, -5, 42, 13 };
            sorter.Sort(more);
            Console.WriteLine($"  built-in sorted:  {string.Join(", ", more)}");

            Console.WriteLine("== Observer ==");
            var feed = new NewsFeed();
            feed.Subscribe(new Reader("khanh"));
            feed.Subscribe(new Reader("nga"));
            feed.Publish("GitHub language stats now more diverse!");
        }
    }
}
