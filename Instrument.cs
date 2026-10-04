namespace TradingApp;

// A tradable market, as reported by the feed.
//
// A 'record' is a class meant for holding data. This one-line form generates a constructor,
// read-only properties (Symbol, TickSize), value-based equality and a readable ToString().
//
// TickSize is the smallest price step the market allows (e.g. 0.01). The order book and ladder
// use it to convert between prices and whole-number "ticks".
public sealed record Instrument(string Symbol, double TickSize);
