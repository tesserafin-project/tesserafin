import { expect, test, type Page } from '@playwright/test';

/**
 * Real-browser first-run onboarding gate for the distributable Tesserafin image.
 *
 * WHY THIS EXISTS
 *
 *   The A3 install validation was false-green: `docker/compose-smoke.sh` polled
 *   `/System/Info/Public` and concluded the install worked, while the published
 *   image actually ran with `--nowebclient` and served the Swagger API docs at
 *   `/`. API reachability is not browser-installability (#115, blocking #89).
 *
 * WHAT IS DELIBERATELY NOT DONE HERE
 *
 *   Onboarding is NEVER driven through the `/Startup/*` API. Doing so would
 *   re-create exactly the class of false-green this gate exists to kill: those
 *   calls succeed against an image with no web client at all. Every step below
 *   goes through the rendered UI of the bundled bundle. The only direct HTTP
 *   calls are read-only assertions about what the server serves.
 *
 * PRECONDITION
 *
 *   The target container was started on pristine `/config` and `/data` and has
 *   NOT been pre-seeded. `docker/browser-onboarding.sh` guarantees that and
 *   asserts `StartupWizardCompleted === false` before this file runs.
 */

const ADMIN_USER = process.env.TESSERAFIN_ADMIN_USER || 'tesserafin-gate-admin';
const ADMIN_PASSWORD = process.env.TESSERAFIN_ADMIN_PASSWORD || 'Gate-Onboarding-Passw0rd!';
const SERVER_NAME = process.env.TESSERAFIN_SERVER_NAME || 'Tesserafin Gate Server';
const MEDIA_PATH = process.env.TESSERAFIN_MEDIA_PATH || '/media';

/** Prefer the visible instance when several retained views match a selector. */
const visible = (page: Page, selector: string) =>
    page.locator(selector).filter({ visible: true }).first();

/**
 * The "Next"/"Finish" control of the wizard step identified by `anchor`.
 *
 * Addressing it by visibility alone is not enough. The view manager keeps
 * previously visited wizard pages in the DOM, and `waitForURL` resolves on the
 * route change — before the outgoing view is torn down. In that window a stale
 * `.button-submit` can still measure as visible, get picked, and then go
 * invisible mid-click, which hangs until timeout. (Two consecutive steps even
 * share `id="wizardSettingsPage"`, so ids are not a reliable discriminator
 * either.)
 *
 * So each step is anchored on an element that ONLY that step has, and the button
 * is resolved as that anchor's own `.wizardPage` ancestor's submit control. It
 * cannot resolve to a different step by construction.
 */
const stepSubmit = (page: Page, anchor: string) =>
    visible(page, anchor)
        .locator('xpath=ancestor::*[contains(@class,"wizardPage")][1]')
        .locator('.button-submit');

/** Waits for a wizard step to be settled, then advances past it. */
async function advanceStep(page: Page, anchor: string) {
    await expect(visible(page, anchor)).toBeVisible({ timeout: 60_000 });
    await waitForIdle(page);
    await stepSubmit(page, anchor).click();
}

/**
 * Waits for the app's global loading spinner to clear.
 *
 * Every wizard step calls `loading.show()` in `viewshow`, fetches its state from
 * `Startup/*`, PREFILLS ITS INPUTS from the response and only then calls
 * `loading.hide()`. Typing before that lands is a lost update: the prefill
 * overwrites what the test just entered. The spinner element carries
 * `mdlSpinnerActive` exactly while a load is in flight
 * (`src/components/loading/loading.ts`), so it is the honest idle signal.
 */
async function waitForIdle(page: Page) {
    await expect(page.locator('.docspinner.mdlSpinnerActive')).toHaveCount(0, { timeout: 60_000 });
}

/**
 * Fills a field and proves the value survived, so a late prefill cannot silently
 * revert it. Without this the wizard submits the server's defaults while the test
 * reports success — a false-green of exactly the kind this gate exists to kill.
 */
async function settledFill(page: Page, selector: string, value: string) {
    await waitForIdle(page);
    const field = visible(page, selector);
    await expect(field).toBeVisible();
    await field.fill(value);
    await expect(field).toHaveValue(value);
}

/**
 * Reads /System/Info/Public with case-insensitive keys.
 *
 * The startup SetupServer answers in camelCase ("startupWizardCompleted") while
 * the running server answers in PascalCase ("StartupWizardCompleted"). A
 * case-sensitive read would return undefined against one of them, which is
 * falsy — i.e. it would silently look like "onboarding not complete" and could
 * mask a real regression.
 */
async function publicSystemInfo(baseURL: string) {
    const response = await fetch(new URL('/System/Info/Public', baseURL));
    expect(response.ok, `/System/Info/Public returned ${response.status}`).toBe(true);
    const raw = (await response.json()) as Record<string, unknown>;
    const info: Record<string, unknown> = {};
    for (const [key, value] of Object.entries(raw)) info[key.toLowerCase()] = value;
    return {
        wizardCompleted: info.startupwizardcompleted,
        serverName: info.servername,
        productName: info.productname,
        version: info.version
    };
}

test.describe.serial('first-run onboarding through the browser', () => {
    test('the server serves the web client, not API documentation', async ({ page, baseURL }) => {
        // `/` is a 302 from BaseUrlRedirectionMiddleware to `/web/` — that hop is
        // the fix (with `--nowebclient` the same middleware sends `/` to
        // `/api-docs/swagger`). Playwright follows it, so the assertions below
        // describe the final document.
        const response = await page.goto('/');
        expect(response, 'no response for /').not.toBeNull();
        expect(response!.status(), '/ did not resolve to 200').toBe(200);

        const contentType = response!.headers()['content-type'] || '';
        expect(contentType, `/ content-type was "${contentType}"`).toContain('text/html');

        expect(page.url(), '/ did not land on the web client').toContain('/web/');
        expect(page.url(), '/ landed on the API documentation').not.toContain('api-docs');

        const html = await page.content();
        expect(html, 'the served document looks like Swagger/ReDoc API documentation')
            .not.toMatch(/swagger-ui|redoc|Tesserafin API<\/title>/i);
        await expect(page).toHaveTitle(/tesserafin/i);
    });

    test('a pristine server presents the real first-run wizard', async ({ page, baseURL }) => {
        const info = await publicSystemInfo(baseURL!);
        expect(info.wizardCompleted, 'the server was already onboarded before the browser ran')
            .toBeFalsy();

        await page.goto('/');
        // ConnectionRequired bounces an un-onboarded server to the wizard.
        await page.waitForURL(/\/wizard\/start/, { timeout: 60_000 });
        await expect(page.locator('#wizardStartPage')).toBeVisible();
        await expect(page.locator('#txtServerName')).toBeVisible();
        await expect(page.locator('#selectLocalizationLanguage')).toBeVisible();
    });

    test('the onboarding path is reached and no Update Required surface is rendered', async ({ page, baseURL }) => {
        // Regression guard for tesserafin-project/tesserafin-web#65. The web client used to take
        // its minimum server version from `@jellyfin/sdk/lib/versions` ('10.10.0'), so a Tesserafin
        // server reporting 1.0.0 resolved to `ConnectionState.ServerUpdateNeeded` and
        // `ConnectionErrorPage` rendered `#connectionErrorPage` with the `Update Required` heading
        // instead of the wizard. The candidate image 1.0.0-dev.965fadf37e20 failed A3 and A7 that
        // way. The wizard assertions above would also have caught it, but only implicitly; this
        // test names both halves of the contract explicitly.
        const info = await publicSystemInfo(baseURL!);
        expect(info.wizardCompleted, 'the server was already onboarded before this test ran')
            .toBeFalsy();
        expect(info.version, 'the server did not report a version').toBeTruthy();

        await page.goto('/');

        // Half 1 — the onboarding path was reached.
        await page.waitForURL(/\/wizard\/start/, { timeout: 60_000 });
        await expect(page.locator('#wizardStartPage')).toBeVisible();

        // Half 2 — the ServerUpdateNeeded surface was not rendered.
        await expect(
            page.locator('#connectionErrorPage'),
            `the connection error page was rendered against a server reporting ${info.version}`
        ).toHaveCount(0);
        const body = (await page.locator('body').innerText()).toLowerCase();
        expect(body, 'the browser rendered the "Update Required" surface')
            .not.toContain('update required');
        expect(body, 'the browser rendered the ServerUpdateNeeded message')
            .not.toContain('this server needs to be updated');
    });

    test('the browser can complete onboarding end to end', async ({ page, baseURL }) => {
        await page.goto('/');
        await page.waitForURL(/\/wizard\/start/, { timeout: 60_000 });

        // --- step 1: server name + display language ---------------------------
        // Anchors below are elements unique to one step — see stepSubmit().
        await settledFill(page, '#txtServerName', SERVER_NAME);
        await advanceStep(page, '#txtServerName');

        // --- step 2: the initial administrator account ------------------------
        await page.waitForURL(/\/wizard\/user/, { timeout: 60_000 });
        // The controller prefills #txtUsername and #txtManualPassword from
        // GET Startup/User after the view is shown; settledFill waits that out.
        await settledFill(page, '#txtUsername', ADMIN_USER);
        await settledFill(page, '#txtManualPassword', ADMIN_PASSWORD);
        await settledFill(page, '#txtPasswordConfirm', ADMIN_PASSWORD);
        await advanceStep(page, '#txtUsername');

        // --- step 3: add /media as a library ----------------------------------
        await page.waitForURL(/\/wizard\/library/, { timeout: 60_000 });
        await expect(visible(page, '#divVirtualFolders')).toBeVisible();

        await visible(page, '#addLibrary').click();
        const dialog = page.locator('.addLibraryForm');
        await expect(dialog).toBeVisible();

        await dialog.locator('#selectCollectionType').selectOption('movies');
        await settledFill(page, '.addLibraryForm #txtValue', 'Gate Movies');

        await dialog.locator('.btnAddFolder').click();
        const picker = page.locator('#txtDirectoryPickerPath');
        await expect(picker).toBeVisible();
        await settledFill(page, '#txtDirectoryPickerPath', MEDIA_PATH);
        // The directory picker is its own dialog; submit only within it.
        await page.locator('.dialog:visible .formDialogFooter .button-submit:visible').last().click();

        // The chosen folder must actually be listed before the library is saved,
        // otherwise a library with no path would be created and the assertion
        // below would pass vacuously.
        await expect(dialog.locator('.folderList')).toContainText(MEDIA_PATH);

        await dialog.locator('.btnSubmit').click();
        await expect(page.locator('#divVirtualFolders')).toContainText('Gate Movies', { timeout: 60_000 });

        await advanceStep(page, '#divVirtualFolders');

        // --- step 4: metadata language / country -------------------------------
        // Anchored on #selectLanguage: this step and the next one share
        // id="wizardSettingsPage", so only their distinct fields tell them apart.
        await page.waitForURL(/\/wizard\/settings/, { timeout: 60_000 });
        await advanceStep(page, '#selectLanguage');

        // --- step 5: remote access ---------------------------------------------
        await page.waitForURL(/\/wizard\/remoteaccess/, { timeout: 60_000 });
        await advanceStep(page, '#chkRemoteAccess');

        // --- step 6: finish -----------------------------------------------------
        await page.waitForURL(/\/wizard\/finish/, { timeout: 60_000 });
        await expect(visible(page, '#wizardFinishPage')).toBeVisible();
        await waitForIdle(page);
        await visible(page, '.btnWizardNext').click();

        // Completing the wizard bounces to the login page of the now-onboarded server.
        await page.waitForURL(/\/login|\/home/, { timeout: 90_000 });

        const info = await publicSystemInfo(baseURL!);
        expect(info.wizardCompleted, 'the server does not report onboarding as complete')
            .toBe(true);
        expect(info.serverName).toBe(SERVER_NAME);
    });

    test('the onboarded server serves the sign-in page and the created account works', async ({
        page,
        baseURL
    }) => {
        // The web client must still be the thing served after onboarding, and it
        // must reflect the state the browser just created.
        await page.goto('/');
        await page.waitForURL(/\/login/, { timeout: 60_000 });
        await expect(visible(page, 'form.manualLoginForm')).toBeVisible();
        await expect(page).toHaveTitle(/tesserafin/i);
        // The server name set during onboarding is asserted from
        // /System/Info/Public in the previous test; the header renders it
        // asynchronously and is not a dependable signal here.

        // The credentials were created THROUGH THE WIZARD UI in the previous
        // test; this only checks that the resulting account is usable. Driving
        // the sign-in form itself proved flaky against the retained-view manager
        // and would add no coverage of the packaging defect this gate exists for,
        // so it is verified at the API instead — deliberately, and only for
        // verification. No part of ONBOARDING is done through the API.
        const response = await fetch(new URL('/Users/AuthenticateByName', baseURL!), {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                Authorization:
                    'MediaBrowser Client="tesserafin-browser-gate", Device="gate", DeviceId="tesserafin-browser-gate", Version="1.0.0"'
            },
            body: JSON.stringify({ Username: ADMIN_USER, Pw: ADMIN_PASSWORD })
        });
        expect(
            response.status,
            `authenticating the browser-created admin returned ${response.status}`
        ).toBe(200);
        const session = (await response.json()) as Record<string, unknown>;
        expect(session.AccessToken, 'no access token for the browser-created admin').toBeTruthy();
    });
});
