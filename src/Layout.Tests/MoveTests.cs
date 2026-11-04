using Expanse;

static class MoveTests
{
    public static void Run(Action<string, bool, string> expect)
    {
        void Is(string name, bool got, bool want) => expect($"moves: {name}", got == want, $"want {want}");

        var none = new MovesUnderway();
        Is("nothing sent: nothing underway", none.Any, false);
        Is("nothing sent: nothing explained", none.Explains(0, 0, 0), false);

        // The bug: lifted to the screen, then sent back; a sync reads it before it has gone.
        var moves = new MovesUnderway();
        moves.Sent(-5000, 20, 100, 20, now: 1000);
        moves.Sent(100, 20, -5000, 20, now: 1300);
        Is("still at the spot it was lifted to", moves.Explains(100, 20, 1367), true);
        Is("still at the spot it was lifted from", moves.Explains(-5000, 20, 1367), true);
        Is("anywhere else, someone else moved it", moves.Explains(400, 300, 1367), false);
        Is("underway", moves.Any, true);

        Is("patience: just inside", moves.Explains(100, 20, 1300 + MovesUnderway.PatienceMs - 1), true);
        Is("patience: over, it's taken at its word", moves.Explains(100, 20, 1300 + MovesUnderway.PatienceMs), false);
        Is("patience over: a move may still land", moves.Any, true);

        moves.Landed();
        Is("landed: nothing underway", moves.Any, false);
        Is("landed: an old spot is now someone else's doing", moves.Explains(100, 20, 1400), false);
    }
}
