namespace Noogen.Backlog.Mcp
{
    /// <summary>
    /// The one thing a caller is told without asking.
    ///
    /// `server/discover` carries an optional <c>instructions</c> string, and it is the only slot on
    /// this surface that costs something in every conversation whether the backlog is touched or
    /// not. So it holds what a model cannot work out from a tool definition and would otherwise get
    /// wrong: that the tab is the state, that reading is cheap if you ask for less, that filing
    /// without searching first is how duplicates happen, and that everything else is one call away.
    ///
    /// It does not restate the verbs. `help` does that on demand, and repeating it here would be
    /// the same list in two places, one of which cannot be generated from the catalog.
    /// </summary>
    public static class ServerGuidance
    {
        public const string Instructions =
            "The Noogen backlog: a WSJF-prioritized Kanban board of work tickets, each one a "
            + "document of prose. The 'backlog' tool holds them and is the whole of the access "
            + "you need — reading or writing a ticket involves no other tool and no file access.\n"
            + "\n"
            + "Work moves Backlog -> In Progress -> Archive, and the tab a ticket lives on is its "
            + "state. There is no status field to set: 'start', 'block', 'unblock', 'review', "
            + "'archive' and 'restore' are how a ticket moves, and only unstarted work is ranked.\n"
            + "\n"
            + "Read cheaply. 'next' answers \"what should I work on?\", 'wip' what is in flight, "
            + "'show' one ticket and the whole text of its document — with 'section' for one "
            + "heading of it. The list-shaped verbs take 'top' and 'fields' to return less.\n"
            + "\n"
            + "Before filing anything, run 'find'. It searches every tab including the archive, and "
            + "the ticket usually already exists.\n"
            + "\n"
            + "Ask before guessing: 'help' writes the whole surface, 'help' with a verb writes one "
            + "of them, and 'help' with a topic reads a guide on writing tickets and scoring them. "
            + "Every refusal names what the verb actually accepts.";
    }
}
