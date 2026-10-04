using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TradingApp;

// One row in the watchlist. It implements INotifyPropertyChanged so the grid updates when Price changes.
// Symbol never changes after construction, so it needs no change notification.
// Price does change, so its setter raises PropertyChanged.
public class Quote : INotifyPropertyChanged
{
    private double _price;

    public Quote(string symbol, double price)
    {
        Symbol = symbol;
        _price = price;
    }

    public string Symbol { get; }

    public double Price
    {
        get => _price;
        set
        {
            if (_price == value) return;
            _price = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
