// Copyright 2026 (c) Ayase Minori, Umamusume Racing Society Contributors
// Licensed under MIT License
namespace Cesario
{
    /// <summary>
    /// Represents a state of a Promise, conformant to the Promises/A+ specification.
    /// </summary>
    public enum PromiseState
    {
        /// <summary>
        /// Represents a Promise that is still waiting to be fulfilled or rejected.
        /// </summary>
        Pending,
        /// <summary>
        /// Represents a Promise that is fulfilled and has a resulting value ready.
        /// </summary>
        Fulfilled,
        /// <summary>
        /// Represents a Promise that has rejected, with the error value.
        /// </summary>
        Rejected
    }
}