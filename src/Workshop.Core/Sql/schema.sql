-- The workshop database. Statuses, part states and timestamps are stored as readable text:
-- statuses use the display names in JobStatusNames, and timestamps are ISO 8601 in UTC
-- (yyyy-MM-ddTHH:mm:ss+00:00), so they sort as text.

CREATE TABLE customers (
    id    TEXT PRIMARY KEY,
    name  TEXT NOT NULL,
    phone TEXT NOT NULL
);

CREATE TABLE devices (
    id          TEXT PRIMARY KEY,
    customer_id TEXT NOT NULL REFERENCES customers (id),
    kind        TEXT NOT NULL CHECK (kind IN ('laptop', 'desktop', 'phone', 'tablet', 'printer', 'other')),
    model       TEXT NOT NULL,
    serial      TEXT NOT NULL
);

CREATE TABLE parts (
    id    TEXT PRIMARY KEY,
    name  TEXT NOT NULL,
    stock INTEGER NOT NULL CHECK (stock >= 0)
);

CREATE TABLE jobs (
    id        TEXT PRIMARY KEY,
    device_id TEXT NOT NULL REFERENCES devices (id),
    fault     TEXT NOT NULL,
    status    TEXT NOT NULL CHECK (status IN ('booked in', 'diagnosing', 'waiting on parts', 'in repair',
                                              'ready', 'collected', 'cancelled')),
    booked_at TEXT NOT NULL
);

-- A part can appear on a job more than once; rows keep the order they were added in (rowid).
CREATE TABLE job_parts (
    job_id   TEXT NOT NULL REFERENCES jobs (id),
    part_id  TEXT NOT NULL REFERENCES parts (id),
    quantity INTEGER NOT NULL CHECK (quantity >= 1),
    state    TEXT NOT NULL CHECK (state IN ('fitted', 'on order'))
);

CREATE INDEX job_parts_by_job ON job_parts (job_id);

CREATE TABLE notes (
    job_id TEXT NOT NULL REFERENCES jobs (id),
    at     TEXT NOT NULL,
    text   TEXT NOT NULL
);

CREATE INDEX notes_by_job ON notes (job_id);
