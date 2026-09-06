/**
 * Regression coverage for boolean UIA attributes (`IsEnabled`, `IsOffscreen`, ...) in
 * both `getPageSource` and the XPath engine.
 *
 * `PageSourceCommands.CBool` and `XPathCommands.ReadCached`/`ReadLive` read these via
 * `GetCachedPropertyValue`/`GetCurrentPropertyValue(pid)` and test `value is int`. UIA
 * hands VT_BOOL properties back as a boxed `bool`, not `int` (see
 * `ElementCommands.cs` `getProperty`, which normalises `bool b => b` before
 * `int i => i`), so that check is always false and every boolean attribute renders as
 * "False"/"false" regardless of the element's real state. The deleted typed accessors
 * (`element.CurrentIsEnabled != 0`) were correct.
 *
 * Target: Windows Calculator's `num1Button`, which is always enabled and always
 * on-screen once the app is open — a stable "known true/false" fixture.
 */
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import type { Browser } from 'webdriverio';
import { createCalculatorSession, quitSession } from './helpers/session.js';

describe('boolean UIA attributes (IsEnabled/IsOffscreen) reflect real element state', () => {
    let driver: Browser;

    beforeAll(async () => {
        driver = await createCalculatorSession();
        await driver.$('~num1Button').waitForExist({ timeout: 15_000 });
    });

    afterAll(async () => {
        await quitSession(driver);
    });

    it('getPageSource reports IsEnabled="True" for an enabled, on-screen button', async () => {
        const source = await driver.getPageSource();
        const match = source.match(/AutomationId="num1Button"[^>]*/);
        expect(match).not.toBeNull();
        const tag = match![0];
        expect(tag).toMatch(/IsEnabled="True"/);
        expect(tag).toMatch(/IsOffscreen="False"/);
    });

    it('XPath finds an element by a true boolean attribute', async () => {
        const el = await driver.$('//Button[@AutomationId="num1Button" and @IsEnabled="true"]');
        expect(await el.isExisting()).toBe(true);
    });

    it('XPath excludes it when the boolean attribute is asserted false', async () => {
        const els = await driver.$$('//Button[@AutomationId="num1Button" and @IsEnabled="false"]');
        expect(els.length).toBe(0);
    });
});
