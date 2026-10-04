-- Made-up records only. Phone numbers are in the 555-01xx range reserved for fiction.
-- 8 customers, 12 devices, 15 jobs (at least one in every status) and 10 parts (two out of stock).
-- Stock is what is on the shelf now: fitted parts were already taken from it.

INSERT INTO customers (id, name, phone) VALUES
    ('C-001', 'Sam Rivera',            '555-0101'),
    ('C-002', 'Henderson Family',      '555-0102'),
    ('C-003', 'Priya Natarajan',       '555-0103'),
    ('C-004', 'Tom Okafor',            '555-0104'),
    ('C-005', 'Lena Fischer',          '555-0105'),
    ('C-006', 'Harbour Street Bakery', '555-0106'),
    ('C-007', 'Marco Bellini',         '555-0107'),
    ('C-008', 'Aiko Tanaka',           '555-0108');

INSERT INTO devices (id, customer_id, kind, model, serial) VALUES
    ('D-001', 'C-001', 'laptop',  'Aster Book 14',  'AB14-7731'),
    ('D-002', 'C-001', 'phone',   'Nimbus 8',       'NB8-20419'),
    ('D-003', 'C-002', 'printer', 'Inkwell 300',    'IW300-5512'),
    ('D-004', 'C-003', 'laptop',  'Corvid Pro 16',  'CP16-0938'),
    ('D-005', 'C-003', 'tablet',  'Slate 11',       'SL11-4410'),
    ('D-006', 'C-004', 'desktop', 'Tower M5',       'TM5-8823'),
    ('D-007', 'C-005', 'phone',   'Nimbus 7 Mini',  'NB7M-3302'),
    ('D-008', 'C-006', 'printer', 'LabelJet 2',     'LJ2-1187'),
    ('D-009', 'C-006', 'desktop', 'Tower M3',       'TM3-6650'),
    ('D-010', 'C-007', 'laptop',  'Aster Book 13',  'AB13-2291'),
    ('D-011', 'C-008', 'tablet',  'Slate 9',        'SL9-7014'),
    ('D-012', 'C-008', 'other',   'Orbit Watch 2',  'OW2-5546');

INSERT INTO parts (id, name, stock) VALUES
    ('P-01', 'Laptop battery',        4),
    ('P-02', 'Phone screen assembly', 0),
    ('P-03', 'Laptop keyboard',       3),
    ('P-04', 'Printhead',             0),
    ('P-05', 'Power supply 450 W',    2),
    ('P-06', 'SSD 1 TB',              5),
    ('P-07', 'Charging port',         6),
    ('P-08', 'Tablet glass panel',    2),
    ('P-09', 'Cooling fan',           4),
    ('P-10', 'Paper feed roller',     8);

INSERT INTO jobs (id, device_id, fault, status, booked_at) VALUES
    ('J-1001', 'D-006', 'No power after a storm',                     'collected',        '2026-08-18T09:10:00+00:00'),
    ('J-1002', 'D-007', 'Cracked screen',                             'cancelled',        '2026-08-25T10:30:00+00:00'),
    ('J-1003', 'D-010', 'Hinge loose on the left side',               'collected',        '2026-09-02T14:05:00+00:00'),
    ('J-1004', 'D-008', 'Paper feed roller worn',                     'collected',        '2026-09-05T11:20:00+00:00'),
    ('J-1005', 'D-010', 'Battery drains in under an hour',            'ready',            '2026-09-12T08:45:00+00:00'),
    ('J-1006', 'D-004', 'Several keyboard keys stick',                'in repair',        '2026-09-15T13:00:00+00:00'),
    ('J-1007', 'D-002', 'Screen flickers, dead pixels along the top', 'waiting on parts', '2026-09-18T09:30:00+00:00'),
    ('J-1008', 'D-003', 'Paper jams on every page',                   'diagnosing',       '2026-09-22T10:15:00+00:00'),
    ('J-1009', 'D-008', 'Prints faint streaks',                       'waiting on parts', '2026-09-24T15:40:00+00:00'),
    ('J-1010', 'D-009', 'Overheats and shuts down',                   'in repair',        '2026-09-25T09:05:00+00:00'),
    ('J-1011', 'D-007', 'Charging port loose',                        'in repair',        '2026-09-26T16:20:00+00:00'),
    ('J-1012', 'D-006', 'Slow to start, disk clicking',               'ready',            '2026-09-27T12:10:00+00:00'),
    ('J-1013', 'D-005', 'Glass cracked in one corner',                'booked in',        '2026-09-29T10:00:00+00:00'),
    ('J-1014', 'D-011', 'Will not charge',                            'diagnosing',       '2026-09-30T11:35:00+00:00'),
    ('J-1015', 'D-012', 'Strap clasp broken',                         'booked in',        '2026-10-01T09:50:00+00:00');

INSERT INTO job_parts (job_id, part_id, quantity, state) VALUES
    ('J-1001', 'P-05', 1, 'fitted'),
    ('J-1004', 'P-10', 1, 'fitted'),
    ('J-1005', 'P-01', 1, 'fitted'),
    ('J-1006', 'P-03', 1, 'fitted'),
    ('J-1007', 'P-02', 1, 'on order'),
    ('J-1009', 'P-04', 1, 'on order'),
    ('J-1010', 'P-09', 1, 'fitted'),
    ('J-1011', 'P-07', 1, 'fitted'),
    ('J-1012', 'P-06', 1, 'fitted');

INSERT INTO notes (job_id, at, text) VALUES
    ('J-1001', '2026-08-20T15:00:00+00:00', 'Power supply replaced; runs for an hour without fault.'),
    ('J-1002', '2026-08-26T09:00:00+00:00', 'Customer chose not to go ahead with the repair.'),
    ('J-1005', '2026-09-16T10:30:00+00:00', 'Battery replaced and calibrated.'),
    ('J-1007', '2026-09-19T08:40:00+00:00', 'Screen assembly ordered from the supplier.'),
    ('J-1008', '2026-09-22T14:00:00+00:00', 'Rollers look clean; checking the paper sensor next.'),
    ('J-1009', '2026-09-25T09:15:00+00:00', 'Printhead ordered.'),
    ('J-1012', '2026-09-30T16:45:00+00:00', 'Customer called; will collect on Friday.');
