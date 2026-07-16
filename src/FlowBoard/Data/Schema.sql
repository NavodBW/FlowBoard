-- FlowBoard schema v1
-- A board IS a column: workspace -> board -> card. There is no separate column table.
-- Every table carries client-generated TEXT (GUID N-format) primary keys.
-- All timestamps are ISO-8601 UTC strings ("o" round-trip format).

CREATE TABLE IF NOT EXISTS meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS workspaces (
    id           TEXT PRIMARY KEY,
    name         TEXT    NOT NULL,
    position     INTEGER NOT NULL,
    archived     INTEGER NOT NULL DEFAULT 0,
    created_utc  TEXT    NOT NULL,
    modified_utc TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS boards (
    id           TEXT PRIMARY KEY,
    workspace_id TEXT    NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,
    name         TEXT    NOT NULL,
    accent       TEXT    NOT NULL,
    position     INTEGER NOT NULL,
    wip_limit    INTEGER NOT NULL DEFAULT 0,
    archived     INTEGER NOT NULL DEFAULT 0,
    created_utc  TEXT    NOT NULL,
    modified_utc TEXT    NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_boards_ws ON boards(workspace_id, position);

CREATE TABLE IF NOT EXISTS cards (
    id           TEXT PRIMARY KEY,
    board_id     TEXT    NOT NULL REFERENCES boards(id) ON DELETE CASCADE,
    title        TEXT    NOT NULL,
    description  TEXT    NOT NULL DEFAULT '',
    priority     INTEGER NOT NULL DEFAULT 0,
    start_utc    TEXT    NULL,
    due_utc      TEXT    NULL,
    position     INTEGER NOT NULL,
    archived     INTEGER NOT NULL DEFAULT 0,
    created_utc  TEXT    NOT NULL,
    modified_utc TEXT    NOT NULL,
    touched_utc  TEXT    NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_cards_board ON cards(board_id, position);
CREATE INDEX IF NOT EXISTS ix_cards_archived ON cards(archived);

CREATE TABLE IF NOT EXISTS labels (
    id           TEXT PRIMARY KEY,
    name         TEXT    NOT NULL,
    color        TEXT    NOT NULL,
    position     INTEGER NOT NULL,
    created_utc  TEXT    NOT NULL,
    modified_utc TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS card_labels (
    card_id  TEXT NOT NULL REFERENCES cards(id)  ON DELETE CASCADE,
    label_id TEXT NOT NULL REFERENCES labels(id) ON DELETE CASCADE,
    PRIMARY KEY (card_id, label_id)
);
CREATE INDEX IF NOT EXISTS ix_card_labels_label ON card_labels(label_id);

CREATE TABLE IF NOT EXISTS checklist_items (
    id           TEXT PRIMARY KEY,
    card_id      TEXT    NOT NULL REFERENCES cards(id) ON DELETE CASCADE,
    text         TEXT    NOT NULL,
    done         INTEGER NOT NULL DEFAULT 0,
    position     INTEGER NOT NULL,
    created_utc  TEXT    NOT NULL,
    modified_utc TEXT    NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_checklist_card ON checklist_items(card_id, position);

CREATE TABLE IF NOT EXISTS card_links (
    id           TEXT PRIMARY KEY,
    card_id      TEXT    NOT NULL REFERENCES cards(id) ON DELETE CASCADE,
    kind         INTEGER NOT NULL,
    target       TEXT    NOT NULL,
    display      TEXT    NOT NULL DEFAULT '',
    position     INTEGER NOT NULL,
    created_utc  TEXT    NOT NULL,
    modified_utc TEXT    NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_links_card ON card_links(card_id, position);

CREATE TABLE IF NOT EXISTS activities (
    id           TEXT PRIMARY KEY,
    card_id      TEXT    NOT NULL REFERENCES cards(id) ON DELETE CASCADE,
    kind         INTEGER NOT NULL,
    detail       TEXT    NOT NULL DEFAULT '',
    created_utc  TEXT    NOT NULL,
    modified_utc TEXT    NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_activities_card ON activities(card_id, created_utc);
