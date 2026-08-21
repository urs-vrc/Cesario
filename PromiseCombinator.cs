// Copyright 2026 (c) Ayase Minori, Umamusume Racing Society Contributors
// Licensed under MIT License
using UdonSharp;
using UnityEngine;

namespace Cesario
{
    /// <summary>
    /// Internal coordinator used by <see cref="PromiseFactory.All"/>,
    /// <see cref="PromiseFactory.Race"/>, <see cref="PromiseFactory.AllSettled"/>, and
    /// <see cref="PromiseFactory.Any"/>. Instantiated (or taken from a pool) per call.
    ///
    /// IMPORTANT: this combinator is only returned to the pool once every input
    /// promise has reported in (fulfilled or rejected) via SettledCount, not as soon
    /// as the outer Result settles - those are different moments for All/Any, since
    /// the outer Result can settle while other inputs are still pending and still
    /// hold live Then/Catch subscriptions pointing at this exact instance. If any
    /// input never settles, this combinator is intentionally never returned - a
    /// contained leak (one idle instance) rather than cross-call corruption.
    ///
    /// Follows the same <c>IncomingPromise</c> contract as every other Then/Catch target.
    /// </summary>
    public class PromiseCombinator : UdonSharpBehaviour
    {
        /// <summary>Required by the Then/Catch callback contract.</summary>
        [System.NonSerialized] public Promise IncomingPromise;

        [System.NonSerialized] public Promise Result;
        [System.NonSerialized] public Promise[] Inputs;
        [System.NonSerialized] public CombinatorMode Mode;

        /// <summary>
        /// Meaning depends on Mode:
        /// All        - fulfillment values, indexed to match Inputs.
        /// AllSettled - PromiseOutcome entries, indexed to match Inputs.
        /// Any        - rejection reasons collected so far, indexed to match Inputs.
        /// Race       - unused.
        /// </summary>
        [System.NonSerialized] public object[] Results;

        /// <summary>Count-down of inputs still outstanding. Used by All, AllSettled, Any.</summary>
        [System.NonSerialized] public int Remaining;

        /// <summary>First-rejection-wins guard for All.</summary>
        [System.NonSerialized] public bool AlreadyRejected;

        /// <summary>First-settlement-wins guard for Race, first-fulfillment-wins guard for Any.</summary>
        [System.NonSerialized] public bool AlreadySettled;

        /// <summary>
        /// How many inputs have reported in so far (fulfilled or rejected), regardless
        /// of whether they affected the outer Result. Used to know when it's safe to
        /// return this instance to the pool - see class remarks.
        /// </summary>
        [System.NonSerialized] public int SettledCount;

        /// <summary>
        /// Back-reference so we can return ourselves to the pool.
        /// </summary>
        [System.NonSerialized] public PromiseFactory Factory;

        public void OnOneFulfilled()
        {
            var alreadyDone = Result == null || Result.IsSettled();

            if (!alreadyDone)
            {
                if (Mode == CombinatorMode.Race)
                {
                    AlreadySettled = true;
                    Result.Resolve(IncomingPromise.Value);
                }
                else if (Mode == CombinatorMode.Any)
                {
                    if (!AlreadySettled)
                    {
                        AlreadySettled = true;
                        Result.Resolve(IncomingPromise.Value);
                    }
                }
                else if (Mode == CombinatorMode.AllSettled)
                {
                    var idx = FindIndex(IncomingPromise);
                    if (idx >= 0)
                    {
                        var entry = new object[2];
                        entry[0] = true;
                        entry[1] = IncomingPromise.Value;
                        Results[idx] = entry;
                    }
                    else
                    {
                        Debug.LogError("[PromiseCombinator] Fulfilled promise not found among tracked inputs - ignoring.");
                    }

                    Remaining--;
                    if (Remaining <= 0)
                        Result.Resolve(Results);
                }
                else // All
                {
                    if (!AlreadyRejected)
                    {
                        var idx = FindIndex(IncomingPromise);
                        if (idx >= 0)
                        {
                            Results[idx] = IncomingPromise.Value;
                        }
                        else
                        {
                            Debug.LogError("[PromiseCombinator] Fulfilled promise not found among tracked inputs - ignoring.");
                        }

                        Remaining--;
                        if (Remaining <= 0)
                            Result.Resolve(Results);
                    }
                }
            }

            AdvanceSettledCount();
        }

        public void OnOneRejected()
        {
            var alreadyDone = Result == null || Result.IsSettled();

            if (!alreadyDone)
            {
                if (Mode == CombinatorMode.Race)
                {
                    AlreadySettled = true;
                    Result.Reject(IncomingPromise.Value);
                }
                else if (Mode == CombinatorMode.Any)
                {
                    if (!AlreadySettled)
                    {
                        var idx = FindIndex(IncomingPromise);
                        if (idx >= 0)
                        {
                            Results[idx] = IncomingPromise.Value; 
                        }
                        else
                        {
                            Debug.LogError("[PromiseCombinator] Rejected promise not found among tracked inputs - ignoring.");
                        }

                        Remaining--;
                        if (Remaining <= 0)
                            // every input rejected - reject with all reasons
                            Result.Reject(Results);
                    }
                }
                else if (Mode == CombinatorMode.AllSettled)
                {
                    var idx = FindIndex(IncomingPromise);
                    if (idx >= 0)
                    {
                        var entry = new object[2];
                        entry[0] = false; 
                        entry[1] = IncomingPromise.Value;
                        Results[idx] = entry;
                    }
                    else
                    {
                        Debug.LogError("[PromiseCombinator] Rejected promise not found among tracked inputs - ignoring.");
                    }

                    Remaining--;
                    if (Remaining <= 0)
                        Result.Resolve(Results);
                }
                else
                {
                    if (!AlreadyRejected)
                    {
                        AlreadyRejected = true;
                        Result.Reject(IncomingPromise.Value);
                    }
                }
            }

            AdvanceSettledCount();
        }

        private void AdvanceSettledCount()
        {
            SettledCount++;

            if (Inputs != null && SettledCount >= Inputs.Length)
                ReturnToPool();
        }

        private int FindIndex(Promise p)
        {
            if (Inputs == null) return -1;
            for (var i = 0; i < Inputs.Length; i++)
            {
                if (Inputs[i] == p) return i;
            }
            return -1;
        }

        private void ReturnToPool()
        {
            if (Factory != null)
                Factory.ReturnCombinator(this);
            else
                Destroy(gameObject);
        }

        /// <summary>
        /// Resets the combinator so it can be returned to the pool and reused.
        /// </summary>
        public void ResetState()
        {
            Result = null;
            Inputs = null;
            Results = null;
            Remaining = 0;
            AlreadyRejected = false;
            AlreadySettled = false;
            Mode = CombinatorMode.All;
            IncomingPromise = null;
            SettledCount = 0;
        }
    }
}