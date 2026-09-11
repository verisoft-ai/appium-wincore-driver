import { defineConfig } from 'vitest/config';
import { resolve } from 'node:path';

// Performance benchmark. Opt-in: it only does anything when RUN_PERF=1 is set
// (the suite is describe.skipIf-gated), and it needs a running Appium server with
// this driver plus the wpf-large fixture built in the sibling appium-wincore-test-apps
// checkout. Run with `npm run test:perf`.
//
// The java-agent and .NET-bridge perf suites live in appium-wincore-java-bridge and
// appium-wincore-dotnet-bridge now — each plugin owns its own performance suite; the
// driver only benchmarks what it's actually aware of (native UIA).
export default defineConfig({
    test: {
        globals: true,
        include: ['test/perf/**/*.perf.ts'],
        // Selecting this config is itself the opt-in; the RUN_PERF gate in the spec
        // then also guards against the perf file being picked up by another config.
        env: { RUN_PERF: '1' },
        testTimeout: 600_000,
        hookTimeout: 120_000,
        pool: 'forks',
        poolOptions: {
            forks: {
                singleFork: true,
            },
        },
    },
    resolve: {
        alias: {
            '@': resolve(__dirname, 'lib'),
        },
    },
});
