// Copyright 2026 (c) Ayase Minori, Umamusume Racing Society Contributors
// Licensed under MIT License
using UdonSharp;
using UnityEngine;

namespace Cesario
{
    /// <summary>
    /// Creates `Promise` instances and wires them to a `PromiseScheduler` automatically,
    /// so callers can't forget to assign `Promise.Scheduler` before use.
    ///
    /// One instance is expected per scene. Assign `PromisePrefab` (a prefab with a
    /// `Promise` behaviour on it) and `Scheduler` in the inspector.
    /// </summary>
    public class PromiseFactory : UdonSharpBehaviour
    {
        public GameObject PromisePrefab;
        public PromiseScheduler Scheduler;

        /// <summary>
        /// Instantiates a new, pending Promise wired to this factory's scheduler.
        /// Returns null (and logs) if misconfigured.
        /// </summary>
        public Promise Create()
        {
            if (PromisePrefab == null)
            {
                Debug.LogError("[PromiseFactory] PromisePrefab not assigned.");
                return null;
            }

            if (Scheduler == null)
            {
                Debug.LogError("[PromiseFactory] Scheduler not assigned.");
                return null;
            }

            var go = Instantiate(PromisePrefab);
            var promise = go.GetComponent<Promise>();
            promise.Scheduler = Scheduler;
            return promise;
        }
        
        /// <summary>Creates an already-fulfilled Promise. Useful for chain handlers that
        /// need to return a settled value wrapped for assimilation.</summary>
        public Promise Fulfilled(object value)
        {
            var p = Create();
            if (p != null) p.Resolve(value);
            return p;
        }

        /// <summary>Creates an already-rejected Promise. The idiomatic way to re-reject
        /// from an IPromiseChainHandler.OnRejected implementation - returning normally
        /// otherwise means "recovered" per the Promises/A+ spec.</summary>
        public Promise Rejected(object reason)
        {
            var p = Create();
            if (p != null) p.Reject(reason);
            return p;
        }
    }
}