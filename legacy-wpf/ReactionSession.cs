using System;
using System.Collections.Generic;
using System.Linq;

namespace ReactionTimer;

internal sealed class ReactionSession
{
    public const int EasterEggThreshold = 150;

    private readonly List<int> times = new List<int>(10);

    public int RoundLimit { get; private set; }

    public IList<int> Times { get; private set; }

    public int FalseStarts { get; private set; }

    public bool IsComplete => times.Count == RoundLimit;

    public int Best
    {
        get
        {
            if (times.Count == 0)
            {
                return -1;
            }
            return times.Min();
        }
    }

    public int Average
    {
        get
        {
            if (times.Count == 0)
            {
                return -1;
            }
            return (int)Math.Round(times.Average(), MidpointRounding.AwayFromZero);
        }
    }

    public ReactionSession()
    {
        Times = times.AsReadOnly();
        Reset(5);
    }

    public void Reset(int roundLimit)
    {
        if (roundLimit != 5 && roundLimit != 10)
        {
            throw new ArgumentOutOfRangeException("roundLimit", "Choose 5 or 10 rounds.");
        }

        RoundLimit = roundLimit;
        FalseStarts = 0;
        times.Clear();
    }

    public void RecordFalseStart()
    {
        EnsureActive();
        FalseStarts++;
    }

    public bool RecordReaction(double milliseconds)
    {
        EnsureActive();

        if (double.IsNaN(milliseconds) || milliseconds < 0.0 || milliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException("milliseconds");
        }

        times.Add((int)Math.Ceiling(milliseconds));
        return milliseconds <= EasterEggThreshold;
    }

    private void EnsureActive()
    {
        if (IsComplete)
        {
            throw new InvalidOperationException("Reset the session before recording another attempt.");
        }
    }
}
