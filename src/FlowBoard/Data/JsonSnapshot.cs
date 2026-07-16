using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowBoard.Domain;

namespace FlowBoard.Data;

// Export DTOs are deliberately separate from the domain models: the file format is a
// contract with the user's future self, so it must not drift every time a model changes.

public sealed class SnapshotFile
{
    public int SchemaVersion { get; set; } = FlowBoardStore.SchemaVersion;
    public string App { get; set; } = "FlowBoard";
    public DateTime ExportedUtc { get; set; } = DateTime.UtcNow;
    public List<LabelDto> Labels { get; set; } = new();
    public List<WorkspaceDto> Workspaces { get; set; } = new();
}

public sealed class LabelDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "";
    public int Position { get; set; }
}

public sealed class WorkspaceDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Position { get; set; }
    public bool Archived { get; set; }
    public List<BoardDto> Boards { get; set; } = new();
}

/// <summary>A board is a column; its cards hang directly off it.</summary>
public sealed class BoardDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Accent { get; set; } = "";
    public int Position { get; set; }
    public int WipLimit { get; set; }
    public bool Archived { get; set; }
    public List<CardDto> Cards { get; set; } = new();
}

public sealed class CardDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public int Priority { get; set; }
    public DateTime? DueUtc { get; set; }
    public int Position { get; set; }
    public bool Archived { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public DateTime TouchedUtc { get; set; }
    public List<string> LabelIds { get; set; } = new();
    public List<ChecklistDto> Checklist { get; set; } = new();
    public List<LinkDto> Links { get; set; } = new();
    public List<ActivityDto> Activities { get; set; } = new();
}

public sealed class ChecklistDto
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public int Position { get; set; }
}

public sealed class LinkDto
{
    public string Id { get; set; } = "";
    public int Kind { get; set; }
    public string Target { get; set; } = "";
    public string Display { get; set; } = "";
    public int Position { get; set; }
}

public sealed class ActivityDto
{
    public string Id { get; set; } = "";
    public int Kind { get; set; }
    public string Detail { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
}

public static class JsonSnapshot
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Export(BoardModel model, string path)
    {
        var file = new SnapshotFile
        {
            Labels = model.Labels.Select(l => new LabelDto
            { Id = l.Id, Name = l.Name, Color = l.Color, Position = l.Position }).ToList(),

            Workspaces = model.Workspaces.Select(ws => new WorkspaceDto
            {
                Id = ws.Id, Name = ws.Name, Position = ws.Position, Archived = ws.Archived,
                Boards = ws.Boards.Select(b => new BoardDto
                {
                    Id = b.Id, Name = b.Name, Accent = b.Accent, Position = b.Position,
                    WipLimit = b.WipLimit, Archived = b.Archived,
                    // Archived cards live outside b.Cards, so pull them from the index —
                    // an export that silently dropped the archive would be a bad backup.
                    Cards = model.CardsById.Values
                        .Where(c => c.BoardId == b.Id)
                        .OrderBy(c => c.Position)
                        .Select(ToDto).ToList()
                }).ToList()
            }).ToList()
        };

        // Write-then-rename: an interrupted export can't clobber a good backup.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, Options));
        File.Move(tmp, path, overwrite: true);
    }

    private static CardDto ToDto(Card c) => new()
    {
        Id = c.Id, Title = c.Title, Description = c.Description, Priority = (int)c.Priority,
        DueUtc = c.DueUtc, Position = c.Position, Archived = c.Archived,
        CreatedUtc = c.CreatedUtc, ModifiedUtc = c.ModifiedUtc, TouchedUtc = c.TouchedUtc,
        LabelIds = c.LabelIds.ToList(),
        Checklist = c.Checklist.Select(i => new ChecklistDto
        { Id = i.Id, Text = i.Text, Done = i.Done, Position = i.Position }).ToList(),
        Links = c.Links.Select(l => new LinkDto
        { Id = l.Id, Kind = (int)l.Kind, Target = l.Target, Display = l.Display, Position = l.Position }).ToList(),
        Activities = c.Activities.Select(a => new ActivityDto
        { Id = a.Id, Kind = (int)a.Kind, Detail = a.Detail, CreatedUtc = a.CreatedUtc }).ToList(),
    };

    /// <summary>Replaces everything. Callers must confirm with the user first, and the
    /// undo stack must be cleared afterwards — it refers to objects that no longer exist.</summary>
    public static BoardModel Import(FlowBoardStore store, string path)
    {
        var file = JsonSerializer.Deserialize<SnapshotFile>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("Not a FlowBoard export.");

        if (file.SchemaVersion > FlowBoardStore.SchemaVersion)
            throw new InvalidDataException(
                $"This file was written by a newer FlowBoard (schema v{file.SchemaVersion}).");

        store.InTransaction(tx =>
        {
            store.Clear(tx);

            foreach (var l in file.Labels)
                store.Upsert(tx, new Label { Id = l.Id, Name = l.Name, Color = l.Color, Position = l.Position });

            foreach (var w in file.Workspaces)
            {
                store.Upsert(tx, new Workspace
                { Id = w.Id, Name = w.Name, Position = w.Position, Archived = w.Archived });

                foreach (var b in w.Boards)
                {
                    store.Upsert(tx, new Board
                    {
                        Id = b.Id, WorkspaceId = w.Id, Name = b.Name, Accent = b.Accent,
                        Position = b.Position, WipLimit = b.WipLimit, Archived = b.Archived
                    });

                    foreach (var card in b.Cards)
                    {
                        var entity = new Card
                        {
                            Id = card.Id, BoardId = b.Id, Title = card.Title,
                            Description = card.Description, Priority = (Priority)card.Priority,
                            DueUtc = card.DueUtc, Position = card.Position, Archived = card.Archived,
                            CreatedUtc = card.CreatedUtc, ModifiedUtc = card.ModifiedUtc,
                            TouchedUtc = card.TouchedUtc
                        };
                        foreach (var id in card.LabelIds) entity.LabelIds.Add(id);
                        store.Upsert(tx, entity);
                        store.SetCardLabels(tx, entity);

                        foreach (var i in card.Checklist)
                            store.Upsert(tx, new ChecklistItem
                            { Id = i.Id, CardId = card.Id, Text = i.Text, Done = i.Done, Position = i.Position });

                        foreach (var l in card.Links)
                            store.Upsert(tx, new CardLink
                            {
                                Id = l.Id, CardId = card.Id, Kind = (LinkKind)l.Kind,
                                Target = l.Target, Display = l.Display, Position = l.Position
                            });

                        foreach (var a in card.Activities)
                            store.Upsert(tx, new Activity
                            {
                                Id = a.Id, CardId = card.Id, Kind = (ActivityKind)a.Kind,
                                Detail = a.Detail, CreatedUtc = a.CreatedUtc
                            });
                    }
                }
            }
        });

        return store.Load();
    }
}
