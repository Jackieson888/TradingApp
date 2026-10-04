namespace TradingApp;

// The data model the UI and the feed agree on.
//
// A 'record' is a class whose main job is to hold data. Writing the one-line form below
// gives you, for free: a constructor, read-only properties (Symbol, TickSize), value-based
// Equals, and a readable ToString. Think of it as a frozen TS object type you can construct
// with `new Instrument("AAPL", 0.01)`.
//
// TickSize is the smallest price step for the instrument. The ladder needs it to know how
// far apart its rows are.
public sealed record Instrument(string Symbol, double TickSize);
