// Copyright 2026 (c) Ayase Minori, Umamusume Racing Society Contributors
// Licensed under MIT License
namespace Cesario
{
    /// <summary>
    /// Which aggregation behavior a <see cref="PromiseCombinator"/> instance is running.
    /// </summary>
    public enum CombinatorMode
    {
        /// <summary>Fulfills when all inputs fulfill; rejects as soon as any input rejects.</summary>
        All,
        /// <summary>Settles (fulfilled or rejected) as soon as the first input settles.</summary>
        Race,
        /// <summary>Always fulfills once every input has settled, with a per-input outcome array.</summary>
        AllSettled,
        /// <summary>Fulfills as soon as any input fulfills; rejects only if every input rejects.</summary>
        Any
    }
}