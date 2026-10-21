namespace SwewolfFileExplorer.Helpers;

/// <summary>What happens when a slideshow reaches the end of the list. The numbers are the buttons of the end-of-list question in the picture viewer.</summary>
public enum EndOfListAction { NextFolder = 0, StartOver = 1, Close = 2 }

/// <summary>How a slideshow runs: set in the Play bar of the explorer, handed to the picture viewer.</summary>
/// <param name="Seconds">How long each picture stays.</param>
/// <param name="End">What to do after the last picture.</param>
/// <param name="AskFirst">Ask what to do at the end (a question with a countdown) instead of doing <paramref name="End"/> at once.</param>
/// <param name="TimeoutSeconds">The countdown of that question: the chosen action is taken by itself after this long (0 = wait for the answer).</param>
public sealed record SlideshowOptions(int Seconds, EndOfListAction End, bool AskFirst, int TimeoutSeconds);
