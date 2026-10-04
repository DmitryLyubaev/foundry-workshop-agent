namespace Workshop.Core;

public sealed record Customer(string Id, string Name, string Phone);

public sealed record Device(string Id, string CustomerId, string Kind, string Model, string Serial);

public sealed record JobCard(string Id, string DeviceId, string Fault, JobStatus Status, DateTimeOffset BookedAt);

public sealed record JobPart(string JobId, string PartId, int Quantity, PartState State);

public sealed record Note(string JobId, DateTimeOffset At, string Text);

public sealed record Part(string Id, string Name, int Stock);
