using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TradingApp;

// A plain C# class: no UI types, no XAML. WPF bindings read properties off an
// object like this one. (In MVVM this role is called a "ViewModel".)
//
// INotifyPropertyChanged is an interface with a single member: the PropertyChanged event.
// WPF's binding engine checks whether the DataContext object implements it, and if so
// subscribes to that event. Without it, WPF reads the property once, when the binding
// is first set up, and never looks again -- it has no way to know the value changed.
public class TickerViewModel : INotifyPropertyChanged
{
    private double _price = 100.00;

    public double Price
    {
        get => _price;
        set
        {
            if (_price == value) return;   // no change, nothing to announce
            _price = value;
            OnPropertyChanged();           // announce it: "Price just changed"
        }
    }

    // The event WPF subscribes to. The '?' means it can be null (no subscribers yet).
    public event PropertyChangedEventHandler? PropertyChanged;

    // [CallerMemberName] makes the compiler fill in the name of the calling member
    // ("Price" when called from the Price setter), so you don't type magic strings.
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        // 'this' = who raised it, then which property changed. WPF compares the name
        // to each binding's path and refreshes the matching ones.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
