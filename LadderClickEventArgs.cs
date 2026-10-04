namespace TradingApp;

public enum Side { Buy, Sell }

// STEP 8: what the ladder says when you click it.
//
// "Who should listen, and what should it carry?"
//
// CARRY: the minimum another part of the app needs to act without looking back at the ladder:
//   - Symbol: which instrument's ladder this was
//   - Price:  the price level that was clicked
//   - Side:   Buy or Sell, decided by which column was clicked
// It does NOT carry a quantity, an order type, or anything about the order. Those are decisions
// for whoever handles the click. The ladder only reports "the user pointed at this price and
// side". It doesn't know what an order is.
//
// LISTEN: not the ladder itself, and ideally not MainWindow either. Right now MainWindow
// listens and writes a line to the blotter, which is fine for a demo. In the real app the
// listener should be an order-entry service (something like IOrderGateway.Submit(...)) that
// MainWindow forwards to. That keeps the ladder reusable (it knows nothing about orders) and
// puts the "should we really send this?" logic (risk checks, confirmation) in one place.
//
// EventArgs is the .NET base class for event payloads, and the conventional handler shape is
// (object? sender, TArgs e), like a DOM event listener receiving an Event object.
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
