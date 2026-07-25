import { defineConfig, devices } from '@playwright/test';

/**
 * Browser/onboarding gate for the distributable Tesserafin image
 * (issue #115 / [A1.2], gating #89 / [A3]).
 *
 * This config deliberately declares NO `webServer`. The candidate container is
 * started by docker/browser-onboarding.sh against pristine volumes and its URL is
 * passed in through TESSERAFIN_BASE_URL. The point of the gate is to drive the
 * REAL published image in a REAL browser; a Playwright-managed dev server would
 * prove nothing about what ships.
 */
const baseURL = process.env.TESSERAFIN_BASE_URL;
if (!baseURL) {
    throw new Error('TESSERAFIN_BASE_URL must point at the running candidate container');
}

export default defineConfig({
    testDir: './tests',
    // The onboarding flow is inherently sequential state mutation on one server.
    fullyParallel: false,
    workers: 1,
    forbidOnly: true,
    retries: 0,
    reporter: [['list']],
    timeout: 120_000,
    expect: { timeout: 30_000 },
    use: {
        baseURL,
        headless: true,
        ignoreHTTPSErrors: true,
        actionTimeout: 30_000,
        navigationTimeout: 60_000,
        screenshot: 'only-on-failure',
        trace: 'retain-on-failure',
        video: 'off'
    },
    projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
    outputDir: process.env.TESSERAFIN_GATE_ARTIFACTS || './test-results'
});
