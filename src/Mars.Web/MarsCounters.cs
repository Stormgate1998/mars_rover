﻿using System.Diagnostics.Metrics;

public class MarsCounters
{
    public Counter<long> GameJoins { get; set; }

    public Counter<long> JoinCalls { get; set; }
    public Histogram<double> JoinDuration { get; set; }
}