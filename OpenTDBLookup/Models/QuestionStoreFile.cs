using System;
using System.Collections.Generic;

namespace OpenTDBLookup.Models;

/// <summary>
/// On-disk shape of <c>questions.json</c>. <see cref="SchemaVersion"/> starts
/// at 1; bump it when the file format changes in a non-backwards-compatible
/// way. <see cref="KnownDuplicateCount"/> is additive (defaults to 0 for old
/// files) so existing caches continue to load.
/// </summary>
public sealed record QuestionStoreFile(
    int SchemaVersion,
    DateTimeOffset? LastFullScrape,
    DateTimeOffset? LastCountCheck,
    Dictionary<int, int> CategoryVerifiedCounts,
    List<Question> Questions,
    int KnownDuplicateCount = 0);
