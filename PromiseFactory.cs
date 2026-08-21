// Copyright 2026 (c) Ayase Minori, Umamusume Racing Society Contributors
// Licensed under MIT License
using UdonSharp;
using UnityEngine;

namespace Cesario
{
    /// <summary>
    /// Creates <see cref="Promise"/> instances and wires them to a
    /// <see cref="PromiseScheduler"/> automatically, so callers can't forget to
    /// assign <c>Promise.Scheduler</c> before use.
    ///
    /// One instance is expected per scene. Assign <c>PromisePrefab</c> (a prefab with
    /// a <see cref="Promise"/> behaviour), <c>CombinatorPrefab</c> (a prefab with a
    /// <see cref="PromiseCombinator"/> behaviour), and <c>Scheduler</c> in the inspector.
    ///
    /// Both promises and combinators are pooled to avoid repeated Instantiate/Destroy
    /// costs. Call <see cref="Return"/> when you are finished with a promise so it can
    /// be reused. Combinators return themselves automatically once every input they
    /// were tracking has settled - see <see cref="PromiseCombinator"/> for why that's
    /// not the same moment the outer promise settles.
    /// </summary>
    public class PromiseFactory : UdonSharpBehaviour
    {
        [Header("Prefabs")]
        public GameObject PromisePrefab;
        public GameObject CombinatorPrefab;
        public PromiseScheduler Scheduler;

        [Header("Promise Pool")]
        [Tooltip("Number of promises created up-front at Start.")]
        public int InitialPoolSize = 16;
        [Tooltip("Soft maximum. Beyond this, excess promises are destroyed instead of pooled.")]
        public int MaxPoolSize = 64;

        [Header("Combinator Pool")]
        [Tooltip("Number of All/Race/AllSettled/Any coordinators created up-front at Start.")]
        public int InitialCombinatorPoolSize = 4;
        [Tooltip("Soft maximum for combinator pool.")]
        public int MaxCombinatorPoolSize = 16;

        private Promise[] _pool;
        private int _poolCount = 0;

        private PromiseCombinator[] _combinatorPool;
        private int _combinatorPoolCount = 0;

        void Start()
        {
            if (PromisePrefab == null || CombinatorPrefab == null || Scheduler == null)
            {
                Debug.LogError("[PromiseFactory] PromisePrefab, CombinatorPrefab or Scheduler not assigned.");
                return;
            }

            _pool = new Promise[MaxPoolSize];
            for (var i = 0; i < InitialPoolSize; i++)
                Return(CreateNew());

            _combinatorPool = new PromiseCombinator[MaxCombinatorPoolSize];
            for (var i = 0; i < InitialCombinatorPoolSize; i++)
                ReturnCombinator(CreateNewCombinator());
        }

        #region Pooling

        /// <summary>
        /// Returns a new (or recycled) pending Promise wired to this factory's scheduler.
        /// Prefer this over instantiating a Promise directly.
        /// </summary>
        public Promise Create()
        {
            Promise p;
            if (_poolCount > 0)
            {
                _poolCount--;
                p = _pool[_poolCount];
                _pool[_poolCount] = null;
                p.gameObject.SetActive(true);
                p.ResetState();
            }
            else
            {
                p = CreateNew();
            }
            return p;
        }

        private Promise CreateNew()
        {
            var go = Instantiate(PromisePrefab, transform);
            var p = go.GetComponent<Promise>();
            p.Scheduler = Scheduler;
            p.Factory = this;
            return p;
        }

        /// <summary>
        /// Returns a promise to the pool so it can be reused. Call this only when you
        /// are certain no other Then/Catch/Chain subscriber is still waiting on it -
        /// in practice, this usually means promises you created and consumed
        /// privately, not ones you handed out to other code via Chain.
        /// </summary>
        public void Return(Promise p)
        {
            if (p == null) return;

            if (_poolCount >= MaxPoolSize)
            {
                Destroy(p.gameObject);
                return;
            }

            p.ResetState();
            p.gameObject.SetActive(false);
            _pool[_poolCount] = p;
            _poolCount++;
        }

        /// <summary>
        /// Creates an already-fulfilled Promise. Useful for chain handlers that
        /// need to return a settled value wrapped for assimilation.
        /// </summary>
        public Promise Fulfilled(object value)
        {
            var p = Create();
            if (p != null) p.Resolve(value);
            return p;
        }

        /// <summary>
        /// Creates an already-rejected Promise. The idiomatic way to re-reject
        /// from a chain handler - returning normally otherwise means "recovered"
        /// per the Promises/A+ spec.
        /// </summary>
        public Promise Rejected(object reason)
        {
            var p = Create();
            if (p != null) p.Reject(reason);
            return p;
        }
        
        #endregion
        
        #region Combinator Pooling

        private PromiseCombinator AcquireCombinator()
        {
            PromiseCombinator c;
            if (_combinatorPoolCount > 0)
            {
                _combinatorPoolCount--;
                c = _combinatorPool[_combinatorPoolCount];
                _combinatorPool[_combinatorPoolCount] = null;
                c.gameObject.SetActive(true);
                c.ResetState();
            }
            else
            {
                c = CreateNewCombinator();
            }
            c.Factory = this;
            return c;
        }

        private PromiseCombinator CreateNewCombinator()
        {
            var go = Instantiate(CombinatorPrefab, transform);
            return go.GetComponent<PromiseCombinator>();
        }

        /// <summary>
        /// Returns a combinator to the pool. Called automatically by
        /// <see cref="PromiseCombinator"/> once every input it was tracking has
        /// settled.
        /// </summary>
        public void ReturnCombinator(PromiseCombinator c)
        {
            if (c == null) return;

            if (_combinatorPoolCount >= MaxCombinatorPoolSize)
            {
                Destroy(c.gameObject);
                return;
            }

            c.ResetState();
            c.gameObject.SetActive(false);
            _combinatorPool[_combinatorPoolCount] = c;
            _combinatorPoolCount++;
        }
        #endregion
        
        #region Combinators
        
        /// <summary>
        /// Returns a promise that fulfills when every input fulfills.
        /// The fulfilled value is an <c>object[]</c> containing the results in the
        /// same order as the input array. Rejects as soon as any input rejects
        /// (with that rejection reason).
        ///
        /// Null entries are treated as already fulfilled with <c>null</c>.
        /// An empty or null array fulfills immediately with an empty <c>object[]</c>.
        /// </summary>
        public Promise All(Promise[] promises)
        {
            if (promises == null || promises.Length == 0)
                return Fulfilled(new object[0]);

            var result = Create();
            if (result == null) return null;

            var results = new object[promises.Length];
            var pending = 0;
            var anyRejected = false;
            object rejectReason = null;

            for (var i = 0; i < promises.Length; i++)
            {
                var p = promises[i];
                if (p == null)
                {
                    results[i] = null;
                    continue;
                }

                if (p.State == PromiseState.Rejected)
                {
                    anyRejected = true;
                    rejectReason = p.Value;
                    break;
                }

                if (p.State == PromiseState.Fulfilled)
                    results[i] = p.Value;
                else
                    pending++;
            }

            if (anyRejected)
            {
                result.Reject(rejectReason);
                return result;
            }

            if (pending == 0)
            {
                result.Resolve(results);
                return result;
            }

            var tracker = AcquireCombinator();
            tracker.Result = result;
            tracker.Inputs = promises;
            tracker.Results = results;
            tracker.Remaining = pending;
            tracker.Mode = CombinatorMode.All;

            for (var i = 0; i < promises.Length; i++)
            {
                var p = promises[i];
                if (p == null || p.IsSettled()) continue;
                p.Then(tracker, nameof(PromiseCombinator.OnOneFulfilled));
                p.Catch(tracker, nameof(PromiseCombinator.OnOneRejected));
            }

            return result;
        }

        /// <summary>
        /// Returns a promise that settles as soon as the first input settles
        /// (either fulfills or rejects). The value/reason is taken from the winner.
        ///
        /// An empty or null array rejects with a descriptive reason (the spec leaves
        /// this case as "never settles" - we deliberately diverge, since an
        /// aggregator that can never complete is a bigger footgun in this
        /// environment than a clear immediate rejection).
        /// </summary>
        public Promise Race(Promise[] promises)
        {
            if (promises == null || promises.Length == 0)
                return Rejected("Promise.Race called with empty array");

            var result = Create();
            if (result == null) return null;

            // Fast-path for already-settled inputs
            for (var i = 0; i < promises.Length; i++)
            {
                var p = promises[i];
                if (p == null) continue;

                if (p.State == PromiseState.Fulfilled)
                {
                    result.Resolve(p.Value);
                    return result;
                }
                if (p.State == PromiseState.Rejected)
                {
                    result.Reject(p.Value);
                    return result;
                }
            }

            var tracker = AcquireCombinator();
            tracker.Result = result;
            tracker.Inputs = promises;
            tracker.Mode = CombinatorMode.Race;

            for (var i = 0; i < promises.Length; i++)
            {
                var p = promises[i];
                if (p == null || p.IsSettled()) continue;
                p.Then(tracker, nameof(PromiseCombinator.OnOneFulfilled));
                p.Catch(tracker, nameof(PromiseCombinator.OnOneRejected));
            }

            return result;
        }


        /// <summary>
        /// Returns a promise that always fulfills once every input has settled,
        /// regardless of whether individual inputs fulfilled or rejected. The
        /// fulfilled value is a <c>PromiseOutcome[]</c>, indexed to match the input
        /// array - check <c>outcome.Fulfilled</c> to see which happened, and
        /// <c>outcome.Value</c> for the value or reason accordingly.
        ///
        /// Null entries are treated as already fulfilled with <c>null</c>. An empty
        /// or null array fulfills immediately with an empty array.
        /// </summary>
        public Promise AllSettled(Promise[] promises)
        {
            if (promises == null || promises.Length == 0)
                return Fulfilled(new object[0]);

            var result = Create();
            if (result == null) return null;

            var outcomes = new object[promises.Length];
            var pending = 0;

            for (var i = 0; i < promises.Length; i++)
            {
                var p = promises[i];
                if (p == null)
                {
                    var entry = new object[2];
                    entry[0] = true;
                    entry[1] = null;
                    outcomes[i] = entry;
                    continue;
                }

                if (p.State == PromiseState.Fulfilled)
                {
                    var entry = new object[2];
                    entry[0] = true;
                    entry[1] = p.Value;
                    outcomes[i] = entry;
                }
                else if (p.State == PromiseState.Rejected)
                {
                    var entry = new object[2];
                    entry[0] = false;
                    entry[1] = p.Value;
                    outcomes[i] = entry;
                }
                else
                {
                    pending++;
                }
            }

            if (pending == 0)
            {
                result.Resolve(outcomes);
                return result;
            }

            var tracker = AcquireCombinator();
            tracker.Result = result;
            tracker.Inputs = promises;
            tracker.Results = outcomes;
            tracker.Remaining = pending;
            tracker.Mode = CombinatorMode.AllSettled;

            for (var i = 0; i < promises.Length; i++)
            {
                var p = promises[i];
                if (p == null || p.IsSettled()) continue;
                p.Then(tracker, nameof(PromiseCombinator.OnOneFulfilled));
                p.Catch(tracker, nameof(PromiseCombinator.OnOneRejected));
            }

            return result;
        }

        /// <summary>
        /// Returns a promise that fulfills as soon as the first input fulfills.
        /// Rejects only if every input rejects, with an <c>object[]</c> of all
        /// rejection reasons (indexed to match the input array).
        ///
        /// An empty or null array rejects immediately with an empty reasons array,
        /// matching JS's Promise.any([]) behavior (there's nothing to fulfill from).
        /// </summary>
        public Promise Any(Promise[] promises)
        {
            if (promises == null || promises.Length == 0)
                return Rejected(new object[0]);

            var result = Create();
            if (result == null) return null;

            // Fast-path: any already-fulfilled input wins immediately.
            for (var i = 0; i < promises.Length; i++)
            {
                var p = promises[i];
                if (p != null && p.State == PromiseState.Fulfilled)
                {
                    result.Resolve(p.Value);
                    return result;
                }
            }

            var reasons = new object[promises.Length];
            var pending = 0;

            for (var i = 0; i < promises.Length; i++)
            {
                var p = promises[i];
                if (p == null)
                {
                    reasons[i] = null;
                    continue;
                }

                if (p.State == PromiseState.Rejected)
                {
                    reasons[i] = p.Value;
                }
                else
                {
                    pending++;
                }
            }

            if (pending == 0)
            {
                // Everything was already rejected (or null) and nothing fulfilled.
                result.Reject(reasons);
                return result;
            }

            var tracker = AcquireCombinator();
            tracker.Result = result;
            tracker.Inputs = promises;
            tracker.Results = reasons;
            tracker.Remaining = pending;
            tracker.Mode = CombinatorMode.Any;

            for (var i = 0; i < promises.Length; i++)
            {
                var p = promises[i];
                if (p == null || p.IsSettled()) continue;
                p.Then(tracker, nameof(PromiseCombinator.OnOneFulfilled));
                p.Catch(tracker, nameof(PromiseCombinator.OnOneRejected));
            }

            return result;
        }
        #endregion
    }
}