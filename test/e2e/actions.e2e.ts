import { describe, it, beforeAll, afterAll, beforeEach, expect } from 'vitest';
import type { Browser } from 'webdriverio';
import {
    createCalculatorSession,
    createNotepadSession,
    createCharmapSession,
    getNotepadTextArea,
    quitSession,
    resetCalculator,
    clearNotepad,
} from './helpers/session.js';
import { Key } from '../../lib/enums.js';

describe('W3C Actions API', () => {
    let calc: Browser;
    let notepad: Browser;

    beforeAll(async () => {
        calc = await createCalculatorSession();
    });

    afterAll(async () => {
        await quitSession(calc);
    });

    beforeEach(async () => {
        await resetCalculator(calc);
    });

    describe('key actions (keyDown / keyUp / pause)', () => {
        it('types a digit into Calculator via keyDown/keyUp sequence', async () => {
            await calc.action('key')
                .down('6')
                .up('6')
                .perform();
            const display = await calc.$('~CalculatorResults');
            expect(await display.getText()).toContain('6');
        });

        it('holds Shift then types a letter in Notepad to produce uppercase', async () => {
            notepad = await createNotepadSession();
            await clearNotepad(notepad);
            const textArea = await getNotepadTextArea(notepad);
            await textArea.click();
            await notepad.action('key')
                .down(Key.SHIFT)
                .down('b')
                .up('b')
                .up(Key.SHIFT)
                .perform();
            const text = await textArea.getText();
            expect(text).toContain('B');
            await notepad.keys([Key.BACKSPACE]); // delete 'B' so Notepad closes without a save prompt
            await quitSession(notepad);
        });

        it('pause action in key sequence still types the digit', async () => {
            await calc.action('key')
                .down('1')
                .pause(100)
                .up('1')
                .perform();

            const display = await calc.$('~CalculatorResults');
            await calc.waitUntil(
                async () => (await display.getText()).includes('1'),
                { timeoutMsg: 'CalculatorResults did not show 1 after key sequence with pause' }
            );
        });

    });

    describe('pointer actions (mouse)', () => {
        it('moves to a button center and clicks via pointer sequence', async () => {
            const btn = await calc.$('~num4Button');
            const loc = await btn.getLocation();
            const size = await btn.getSize();
            const cx = Math.round(loc.x + size.width / 2);
            const cy = Math.round(loc.y + size.height / 2);

            await calc.action('pointer')
                .move({ x: cx, y: cy })
                .down()
                .up()
                .perform();

            const display = await calc.$('~CalculatorResults');
            expect(await display.getText()).toContain('4');
        });

        it('performs a double-click via two down/up cycles', async () => {
            const btn = await calc.$('~num5Button');
            await calc.action('pointer')
                .move({ origin: btn })
                .down()
                .up()
                .down()
                .up()
                .perform();

            const display = await calc.$('~CalculatorResults');
            expect(await display.getText()).toContain('55');

        });

        it('right-click using button: 2 in pointer down', async () => {
            const btn = await calc.$('~num1Button');
            await expect(
                calc.action('pointer')
                    .move({ origin: btn })
                    .down({ button: 2 })
                    .up({ button: 2 })
                    .perform()
            ).resolves.not.toThrow();
        });

        it('drags from one button to another via pointerDown, pointerMove, pointerUp', async () => {
            const startBtn = await calc.$('~num1Button');
            const endBtn = await calc.$('~num2Button');
            await expect(
                calc.action('pointer')
                    .move({ origin: startBtn })
                    .down()
                    .move({ origin: endBtn, duration: 300 })
                    .up()
                    .perform()
            ).resolves.not.toThrow();
        });
    });

    describe('tick-based ordering across multiple input sources', () => {
        let notepad: Browser;

        beforeAll(async () => {
            notepad = await createNotepadSession();
        });

        afterAll(async () => {
            await quitSession(notepad);
        });

        beforeEach(async () => {
            await clearNotepad(notepad);
            const textArea = await getNotepadTextArea(notepad);
            await textArea.click();
        });

        it('Shift+Click selects text: Shift (key) is held across the pointer click tick', async () => {
            const textArea = await getNotepadTextArea(notepad);
            await textArea.setValue('hello');
            await notepad.keys([Key.HOME]);

            // key:     [down(Shift), pause,  up(Shift)]
            // pointer: [move,        down,   up       ]
            //                        ^-- tick 1: Shift still held when mouse goes down
            await notepad.actions([
                notepad.action('key').down(Key.SHIFT).pause(0).up(Key.SHIFT),
                notepad.action('pointer').move({ origin: textArea, x: 200, y: 0 }).down().up(),
            ]);

            // Shift held during click selects "hello"; typing X replaces it
            await notepad.keys(['X']);
            expect((await textArea.getText()).trim()).toBe('X');
        });

        it('key + pointer: respect tick order', async () => {
            const textArea = await getNotepadTextArea(notepad);
            await textArea.setValue('hello');
            await notepad.keys([Key.HOME]);

            // key:     [down(Shift), pause,  up(Shift)]
            // pointer: [move,        down,   up       ]
            //                        ^-- tick 1: Shift still held when mouse goes down
            await notepad.actions([
                notepad.action('key').down(Key.SHIFT).pause(0).up(Key.SHIFT),
                notepad.action('pointer').move({ origin: textArea, x: 200, y: 0 }).down().up(),
            ]);

            await notepad.keys(['X']);
            expect((await textArea.getText()).trim()).toBe('X');
        });

        it('wheel scroll moves the viewport: off-screen items become reachable by scrolling', async () => {
            // Character Map's font ComboBox popup has a small, fixed-height dropdown
            // regardless of window size/DPI, and the system font list is always long
            // enough to overflow it — no synthetic content or window resizing needed
            // to force a scrollable viewport (unlike typing N lines into Notepad).
            let charmap: Browser | undefined;
            try {
                charmap = await createCharmapSession();
                const comboBox = await charmap.$('~105');
                await charmap.executeScript('windows: expand', [comboBox]);

                await charmap.waitUntil(
                    async () => {
                        const state = await comboBox.getAttribute('ExpandCollapseState');
                        return state === 'Expanded' || state === 'PartiallyExpanded';
                    },
                    { timeout: 5000, timeoutMsg: 'font ComboBox did not report Expanded state' }
                );

                // ExpandCollapseState flips before the popup's list finishes laying
                // itself out — a fixed pause here was a race that occasionally let the
                // assertions below run against a list that hadn't rendered yet. Poll
                // until it's actually populated and settled instead.
                let items: WebdriverIO.Element[] = [];
                await charmap.waitUntil(
                    async () => {
                        items = await charmap!.$$('//ListItem').getElements();
                        if (items.length <= 20) {
                            return false;
                        }
                        const firstOffscreen = await items[0].getAttribute('IsOffscreen');
                        return String(firstOffscreen).toLowerCase() === 'false';
                    },
                    { timeout: 5000, timeoutMsg: 'font list did not finish populating after expand' }
                );

                const firstItem = items[0];
                const lastItem = items[items.length - 1];

                expect(String(await lastItem.getAttribute('IsOffscreen')).toLowerCase()).toBe('true');

                const loc = await firstItem.getLocation();
                const size = await firstItem.getSize();
                const x = Math.round(loc.x + size.width / 2);
                const y = Math.round(loc.y + size.height / 2);

                // Scroll the popup down via a real wheel action; the last item should
                // become reachable.
                await charmap.actions([
                    charmap.action('wheel').scroll({ x, y, deltaX: 0, deltaY: 100_000, duration: 1000 }),
                ]);
                await charmap.waitUntil(
                    async () => String(await lastItem.getAttribute('IsOffscreen')).toLowerCase() === 'false',
                    { timeoutMsg: 'last font item still off-screen after wheel-scrolling down' }
                );

                // Scroll back up; the first item should become reachable again.
                await charmap.actions([
                    charmap.action('wheel').scroll({ x, y, deltaX: 0, deltaY: -100_000, duration: 1000 }),
                ]);
                await charmap.waitUntil(
                    async () => String(await firstItem.getAttribute('IsOffscreen')).toLowerCase() === 'false',
                    { timeoutMsg: 'first font item still off-screen after wheel-scrolling up' }
                );
            } finally {
                if (charmap) {
                    await quitSession(charmap);
                }
            }
        });
    });
});
