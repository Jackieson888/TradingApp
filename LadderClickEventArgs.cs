namespace TradingApp;

public enum Side { Buy, Sell }

// The payload of LadderControl.PriceClicked: which market, price and side the user clicked.
//
// It deliberately carries no quantity or order type. The ladder only reports "the user pointed at
// this price and side"; deciding what to do with that belongs to whoever handles the event. Today
// that is MainWindow, which writes a line to the order log. A real app would forward it
// to an order-entry service, keeping the ladder reusable and putting checks such as risk limits
// and confirmation in one place.
//
// EventArgs is the .NET base class for event payloads. Handlers have the conventional
// signature (object? sender, LadderClickEventArgs e).
public sealed class LadderClickEventArgs : EventArgs
{
    public LadderClickEventArgs(string symbol, double price, Side side)
    {
        Symbol = symbol;
        Price = price;
        Side = side;
    }

    public string Symbol { get; }
    public double Price { get; }
    public Side Side { get; }
}
