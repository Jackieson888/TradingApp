using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TradingApp;

public partial class MainWindow : Window
{
    // --- Fields: state only. No statements here. ---
    private readonly ObservableCollection<Quote> _quotes = new();
    private readonly TickerViewModel _ticker = new();
    private readonly Random _random = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    // --- Constructor: setup code runs here, after InitializeComponent() has built the XAML. ---
    public MainWindow()
    {
        InitializeComponent();

        DataContext = _ticker;

        foreach (var symbol in new[] { "AAPL", "MSFT", "GOOG", "AMZN", "TSLA" })
        {
            _quotes.Add(new Quote(symbol, 100 + _random.Next(0, 100)));
        }
        WatchlistGrid.ItemsSource = _quotes;

        _timer.Tick += (sender, e) =>
        {
            _ticker.Price += (_random.NextDouble() - 0.5);
        };
        _timer.Start();

        // Step 5 (BeginInvoke version): the worker posts each update to the UI thread.
        var worker = new Thread(() =>
        {
            while (true)
            {
                for (int i = 0; i < _quotes.Count; i++)
                {
                    double newPrice = _quotes[i].Price + (_random.NextDouble() - 0.5);
                    int index = i; double price = newPrice;
                    Dispatcher.BeginInvoke(() => _quotes[index] = new Quote(_quotes[index].Symbol, price));
                }
                Thread.Sleep(100);
            }
        })
        { IsBackground = true };
        worker.Start();
    }
}
