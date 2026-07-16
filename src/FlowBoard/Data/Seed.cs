using FlowBoard.Domain;

namespace FlowBoard.Data;

/// <summary>Creates the first-run content. Idempotent: does nothing if a workspace exists.</summary>
public static class Seed
{
    /// <summary>The four default boards. A board is a column, so this is the whole first-run
    /// layout: four lanes, left to right.</summary>
    private static readonly (string Name, string Accent)[] DefaultBoards =
    {
        ("Now",    "#4C8DFF"),
        ("Next",   "#37C2A8"),
        ("Later",  "#B586F0"),
        ("Parked", "#8A94A6"),
    };

    private static readonly (string Name, string Color)[] DefaultLabels =
    {
        ("Bug",     "#E5484D"),
        ("Feature", "#4C8DFF"),
        ("Chore",   "#8A94A6"),
        ("Blocked", "#F5A524"),
    };

    public static BoardModel EnsureSeeded(FlowBoardStore store, BoardModel model)
    {
        if (model.Workspaces.Count > 0) return model;

        var ws = new Workspace { Name = "Personal", Position = 0 };

        for (var i = 0; i < DefaultBoards.Length; i++)
        {
            var (name, accent) = DefaultBoards[i];
            ws.Boards.Add(new Board
            {
                WorkspaceId = ws.Id,
                Name = name,
                Accent = accent,
                Position = i
            });
        }

        var labels = DefaultLabels
            .Select((l, i) => new Label { Name = l.Name, Color = l.Color, Position = i })
            .ToList();

        store.InTransaction(tx =>
        {
            foreach (var l in labels) store.Upsert(tx, l);
            store.Upsert(tx, ws);
            foreach (var b in ws.Boards) store.Upsert(tx, b);
        });

        foreach (var l in labels) model.Labels.Add(l);
        model.Workspaces.Add(ws);
        model.Index(ws);
        return model;
    }
}
