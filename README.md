# Cesario

**Promise-style async for UdonSharp (VRChat).**

Named after Cesario — a character in *Umamusume: Pretty Derby*, and a real-world racehorse.

If you've used Promises in JavaScript, TypeScript, or a similar language, Cesario should
feel familiar: create a promise, resolve or reject it when your async work finishes,
subscribe with `Then`/`Catch`, and chain follow-up steps. It's adapted for UdonSharp's
constraints (no closures, no `async`/`await`, no custom interfaces), but the mental model —
pending → fulfilled or rejected, once, and callbacks that run off of that — is the same one
you already know.

---

## Why

Sequential async logic in Udon usually turns into a pile of boolean flags and step-counters
threaded through `Update()` or chained `SendCustomEventDelayedFrames` calls, because there's
no `async`/`await` to fall back on. Cesario gives you a state machine and a chaining API
instead, so "do this, then that, then handle failure" reads as a short pipeline rather than
a hand-rolled state machine you have to re-derive every time.

---

## Installation

1. Copy the `Cesario` folder into your UdonSharp project (`Assets/Code/Cesario`).
2. Make sure **UdonSharp** and the **VRChat SDK** are already set up in your project.
3. Add a `PromiseSystem` GameObject to your scene with a `PromiseScheduler` and a
   `PromiseFactory` component on it (see [Scene Setup](#scene-setup)).

---

## Quick Start

### Scene Setup

```
GameObject "PromiseSystem"
├── PromiseScheduler (component)
└── PromiseFactory (component)
       PromisePrefab → Assets/Code/Cesario/Promise.prefab
       Scheduler → (the PromiseScheduler above)
```

`Promise.prefab` just needs a `Promise` component on it, with its Synchronization Method
set to **None** — Cesario is entirely client-local, nothing in it is networked.

### Creating and resolving a promise

```csharp
public class Example : UdonSharpBehaviour
{
    public PromiseFactory Factory;
    [System.NonSerialized] public Promise IncomingPromise;

    private Promise _pending;

    void Start()
    {
        _pending = Factory.Create();
        _pending.Then(this, nameof(OnSuccess));
        _pending.Catch(this, nameof(OnFailure));

        SendCustomEventDelayedSeconds(nameof(ResolveLater), 1f);
    }

    public void ResolveLater() => _pending.Resolve("Hello, Cesario!");
    public void OnSuccess() => Debug.Log($"Fulfilled with: {IncomingPromise.Value}");
    public void OnFailure() => Debug.LogWarning($"Rejected: {IncomingPromise.Value}");
}
```

Every callback target — anything passed to `Then`, `Catch`, or `Chain` — needs a public
`Promise IncomingPromise` field. Cesario populates it right before firing your callback, so
by the time your method runs, `IncomingPromise.Value` has whatever was resolved or rejected.
This stands in for passing the value as a normal argument, since UdonSharp can't dispatch
callbacks with typed parameters the way `.then(value => ...)` does in JS.

### Chaining

```csharp
downloader.StartDownload(url)
    .Chain(parseStep, nameof(ParseStep.OnParsed), nameof(ParseStep.OnParseError))
    .Then(consumer, nameof(Consumer.OnPipelineComplete));
```

`Chain` is what actually returns a new promise you can keep chaining off of — a plain
`Then`/`Catch` call doesn't (more on that below). A chain handler reports its result by
setting a field before its method returns:

```csharp
public class ParseStep : UdonSharpBehaviour
{
    [System.NonSerialized] public Promise IncomingPromise;
    [System.NonSerialized] public object ChainResult;

    public void OnParsed()
    {
        string raw = (string)IncomingPromise.Value;
        ChainResult = ParseIt(raw); // becomes the next promise's value
    }

    public void OnParseError()
    {
        ChainResult = DefaultValue; // recovers - the chain continues fulfilled
    }
}
```

If a step needs to hand off to *another* promise instead of a plain value (say, `OnParsed`
itself kicks off more async work), set `ChainResultAdoptee` to that promise instead of
`ChainResult` — the next step in the chain will wait on it.

---

## Core Types

| Type | What it does |
|---|---|
| `Promise` | The promise itself — state, value, and subscriptions. |
| `PromiseFactory` | Creates promises and wires them up correctly. Always create promises through this rather than instantiating one directly. |
| `PromiseScheduler` | Makes sure callbacks never fire in the same frame that settled the promise. One per scene, lives alongside the factory. |
| `PromiseState` | `Pending`, `Fulfilled`, or `Rejected`. |

## API at a Glance

| Method | What it's for |
|---|---|
| `Factory.Create()` | Make a new, pending promise. |
| `Factory.Fulfilled(value)` / `Factory.Rejected(reason)` | Make an already-settled promise. |
| `promise.Resolve(value)` | Fulfill it. |
| `promise.Reject(reason)` | Reject it. |
| `promise.Then(target, eventName)` | Run something when it fulfills. |
| `promise.Catch(target, eventName)` | Run something when it rejects. |
| `promise.Chain(target, onFulfilledEvent, onRejectedEvent)` | Run a step and get a new promise for the next one. |
| `promise.Adopt(otherPromise)` | Settle this promise by waiting on another one. |
| `promise.IsSettled()` | Check if it's already fulfilled or rejected. |

---

## How faithful is this to real Promises?

Close in spirit, not identical in every detail — mainly because UdonSharp doesn't have
closures, `async`/`await`, or (currently) custom interfaces, so a few things had to be
adapted or simplified:

- **`Then`/`Catch` don't return a new promise** — only `Chain` does. In JavaScript, every
  `.then()` call is chainable; here, plain subscriptions are fire-and-forget, and `Chain` is
  the dedicated tool for building a pipeline. This keeps `Then`/`Catch` cheap (no extra
  promise object created) for the common case of "just notify me," while `Chain` handles the
  case where you actually need a continuation.
- **Callbacks are deferred by one frame, not a JS-style microtask.** They're guaranteed to
  never run synchronously with whatever settled the promise, which is the important part —
  but a long chain resolves over several frames rather than instantly within one tick.
- **No automatic detection of "is this value actually a promise."** If a step needs to wait
  on another promise instead of settling with a plain value, that's explicit — either call
  `Adopt()` directly, or set `ChainResultAdoptee` in a chain handler. Nothing tries to guess.
- **A throwing handler doesn't automatically become a rejection.** If your callback can fail,
  catch it yourself and call `Reject` explicitly.
- **Values are untyped (`object`).** UdonSharp doesn't support generics, so there's no
  `Promise<T>` — the caller and whoever reads `.Value` just need to agree on the type.

None of this is meant to be a drop-in mental model swap from JS Promises — it's close
enough that the patterns transfer, with the differences above being the places where "just
like JS" would lead you wrong.

---

## Known Gaps

- No `Promise.All` / `Promise.Race` yet.
- No object pooling — each `Factory.Create()` call instantiates a GameObject.
- If you need multiple concurrent operations from the same behaviour (rather than one at a
  time), you'll need to track multiple pending promises yourself — Cesario doesn't do this
  for you.

---

## License

MIT License — Copyright 2026 Ayase Minori, Umamusume Racing Society Contributors.