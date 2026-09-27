// Minimal host APIs for a bare V8 engine (no console, performance or timers there).
if (typeof globalThis.console === 'undefined') {
    const noop = () => {};
    globalThis.console = { log: noop, info: noop, warn: noop, error: noop, debug: noop, time: noop, timeEnd: noop, trace: noop };
}
if (typeof globalThis.performance === 'undefined') globalThis.performance = { now: () => Date.now() };
if (typeof globalThis.setTimeout === 'undefined') globalThis.setTimeout = (fn) => { fn(); return 0; };
