namespace SlothSort;

public enum ProposalStatus { Pending, Moved, Skipped, Error }

/// <summary>One suggested tidy-up: move this file to that folder with this name.</summary>
public sealed class Proposal
{
    public string Id { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public DateTime Modified { get; set; }

    public string Folder { get; set; } = Folders.Other;  // destination category folder
    public string NewName { get; set; } = "";              // proposed file name, with extension
    public string Reason { get; set; } = "";               // short, human explanation
    public string Source { get; set; } = "";               // "rule" or "ai" (or "you" after an edit)
    public bool Sensitive { get; set; }                    // looks like ID / financial / medical
    public bool Selected { get; set; } = true;             // included when you click "Tidy"
    public ProposalStatus Status { get; set; } = ProposalStatus.Pending;
    public string? Error { get; set; }
    public double? Seconds { get; set; }                   // AI time spent on this file
}

public sealed class Move
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

/// <summary>A batch of moves you approved, recorded so it can be undone later.</summary>
public sealed class Batch
{
    public string Id { get; set; } = "";
    public DateTime WhenUtc { get; set; }
    public string Folder { get; set; } = "";
    public List<Move> Moves { get; set; } = new();
    public double AiSeconds { get; set; }
    public bool Undone { get; set; }
}

public sealed class Settings
{
    public string Folder { get; set; } = DefaultDownloads();
    /// <summary>Where tidied files go. Empty = a "Sorted" folder inside the folder being tidied.</summary>
    public string Destination { get; set; } = "";
    /// <summary>Also look inside photos/images with the vision model (slower). Off = sort images by name only.</summary>
    public bool ReadImages { get; set; }

    public static string DefaultDownloads() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public string DestinationRoot() =>
        string.IsNullOrWhiteSpace(Destination) ? Path.Combine(Folder, "Sorted") : Destination;
}

/// <summary>What the model returns for one file.</summary>
public sealed class Verdict
{
    public string Folder { get; set; } = Folders.Other;
    public string Title { get; set; } = "";
    public string Date { get; set; } = "";
    public bool Sensitive { get; set; }
    public string Reason { get; set; } = "";
}
