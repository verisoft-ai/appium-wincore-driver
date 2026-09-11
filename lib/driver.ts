/* eslint-disable @typescript-eslint/no-non-null-assertion */
import { BaseDriver, errors } from 'appium/driver';
import { system } from 'appium/support';
import type { ScreenRecorder } from './commands/screen-recorder';
import commands from './commands';
import {
    WincoreDriverConstraints,
    UI_AUTOMATION_DRIVER_CONSTRAINTS
} from './constraints';
import { WincoreServerClient } from './server/client';
import { attachLogFileMirror, LogFileMirror } from './log-file';
import { DRIVER_VERSION } from './version';
import { executeMethodMap } from './execute-method-map';
import {
    assertSupportedEasingFunction
} from './util';
import { locateElements } from './commands/find-via';
import { cssToNativeLocator } from './css';

import type { Chromedriver } from 'appium-chromedriver';
import type { IESession } from './ie/session';
import type {
    DefaultCreateSessionResult,
    DriverData,
    Element,
    ExternalDriver,
    InitialOpts,
    RouteMatcher,
    StringRecord,
    W3CDriverCaps
} from '@appium/types';

type W3CWincoreDriverCaps = W3CDriverCaps<WincoreDriverConstraints>;
type DefaultWindowsCreateSessionResult = DefaultCreateSessionResult<WincoreDriverConstraints>;

type KeyboardState = {
    pressed: Set<string>,
    shift: boolean,
    ctrl: boolean,
    meta: boolean,
    alt: boolean,
}

const LOCATION_STRATEGIES = Object.freeze([
    'id',
    'name',
    'xpath',
    'css selector',
    'tag name',
    'class name',
    'accessibility id',
    '-windows uiautomation',
] as const);

// This is a set of methods and paths that we never want to proxy to Chromedriver.
const CHROMEDRIVER_NO_PROXY: RouteMatcher[] = [
    ['GET', new RegExp('^/session/[^/]+/appium')],
    ['GET', new RegExp('^/session/[^/]+/context')],
    ['GET', new RegExp('^/session/[^/]+/element/[^/]+/rect')],
    ['GET', new RegExp('^/session/[^/]+/orientation')],
    ['POST', new RegExp('^/session/[^/]+/appium')],
    ['POST', new RegExp('^/session/[^/]+/context')],
    ['POST', new RegExp('^/session/[^/]+/orientation')],

    // this is needed to make the windows: and powerShell commands work in web context
    ['POST', new RegExp('^/session/[^/]+/execute$')],
    ['POST', new RegExp('^/session/[^/]+/execute/sync')],

    // NOTE: the log routes (/log, /log/types, /se/log, /se/log/types) are intentionally
    // NOT listed here. The upstream Appium driver template excludes them so the base
    // driver can serve its own server/device logs, but this driver has no getLog
    // implementation — keeping the exclusion just made manage().logs() fail in every
    // context. Letting them proxy means logs work while a WEBVIEW_* context is active.
];

export class AppiumWincoreDriver extends BaseDriver<WincoreDriverConstraints, StringRecord> {
    static executeMethodMap = executeMethodMap;

    serverClient?: WincoreServerClient;
    mouseButtonsDown: Set<number> = new Set();
    keyboardState: KeyboardState = {
        pressed: new Set(),
        alt: false,
        ctrl: false,
        meta: false,
        shift: false,
    };
    chromedriver: Chromedriver | null = null;
    proxyReqRes: ((...args: any) => any) | null = null;
    proxyCommand: ExternalDriver['proxyCommand'] | null = null;
    contexts: string[] = [];
    jwpProxyActive: boolean = false;
    currentContext: string | null = null;
    _screenRecorder: ScreenRecorder | null = null;
    _logFileMirror: LogFileMirror | null = null;
    webviewDevtoolsPort: number | null = null;
    ieSession: IESession | null = null;
    ieContext: boolean = false;
    ieHwnd?: number;

    constructor(opts: InitialOpts = {} as InitialOpts, shouldValidateCaps = true) {
        super(opts, shouldValidateCaps);

        this.locatorStrategies = [...LOCATION_STRATEGIES];
        this.desiredCapConstraints = UI_AUTOMATION_DRIVER_CONSTRAINTS;

        // Bind commands to this instance (not prototype) so each driver instance uses its own
        // server client and state when multiple sessions exist
        for (const key in commands) { // TODO: create a decorator that will do that for the class
            (this as any)[key] = commands[key].bind(this);
        }
    }

    async sendCommand(method: string, params: Record<string, unknown> = {}): Promise<unknown> {
        if (!this.serverClient) {
            throw new errors.UnknownError('WincoreServer is not running.');
        }
        return await this.serverClient.sendCommand(method, params);
    }

    override canProxy(): boolean {
        return true;
    }

    override proxyActive(): boolean {
        return this.jwpProxyActive;
    }

    override getProxyAvoidList(): RouteMatcher[] {
        if (this.jwpProxyActive && this.chromedriver) { return CHROMEDRIVER_NO_PROXY; }
        return [];
    }

    isIEContext(): boolean {
        return this.ieContext && this.ieSession !== null;
    }

    override async findElement(strategy: string, selector: string): Promise<Element> {
        [strategy, selector] = await this.processSelector(strategy, selector);
        return super.findElement(strategy, selector);
    }

    override async findElements(strategy: string, selector: string): Promise<Element[]> {
        [strategy, selector] = await this.processSelector(strategy, selector);
        return super.findElements(strategy, selector);
    }

    override async findElementFromElement(strategy: string, selector: string, elementId: string): Promise<Element> {
        [strategy, selector] = await this.processSelector(strategy, selector);
        return super.findElementFromElement(strategy, selector, elementId);
    }

    override async findElementsFromElement(strategy: string, selector: string, elementId: string): Promise<Element[]> {
        [strategy, selector] = await this.processSelector(strategy, selector);
        return super.findElementsFromElement(strategy, selector, elementId);
    }

    override async findElOrEls(strategy: typeof LOCATION_STRATEGIES[number], selector: string, mult: true, context?: string): Promise<Element[]>;
    override async findElOrEls(strategy: typeof LOCATION_STRATEGIES[number], selector: string, mult: false, context?: string): Promise<Element>;
    override async findElOrEls(strategy: typeof LOCATION_STRATEGIES[number], selector: string, mult: boolean, context?: string): Promise<Element | Element[]> {
        if (this.isIEContext()) {
            if (mult) {
                return this.ieSession!.findElements(strategy, selector) as unknown as Promise<Element[]>;
            }
            try {
                return await this.ieSession!.findElement(strategy, selector) as unknown as Element;
            } catch {
                throw new errors.NoSuchElementError();
            }
        }

        if (strategy === 'css selector') {
            throw new errors.InvalidArgumentError(`Invalid find strategy ${strategy}`);
        }

        return mult
            ? await locateElements(strategy, selector, true, context, this.sendCommand.bind(this))
            : await locateElements(strategy, selector, false, context, this.sendCommand.bind(this));
    }

    override async createSession(
        jwpCaps: W3CWincoreDriverCaps,
        reqCaps?: W3CWincoreDriverCaps,
        w3cCaps?: W3CWincoreDriverCaps,
        driverData?: DriverData[]
    ): Promise<DefaultWindowsCreateSessionResult> {
        if (!system.isWindows()) {
            this.log.errorWithException('Windows UI Automation tests only run on Windows.');
        }

        if (typeof w3cCaps?.alwaysMatch?.['appium:appTopLevelWindow'] === 'number') {
            w3cCaps.alwaysMatch['appium:appTopLevelWindow'] = String(w3cCaps.alwaysMatch['appium:appTopLevelWindow']);
        }

        if (typeof w3cCaps?.firstMatch?.some['appium:appTopLevelWindow'] === 'number') {
            w3cCaps.firstMatch['appium:appTopLevelWindow'] = w3cCaps.firstMatch['appium:appTopLevelWindow'].map(String);
        }

        // Warn when the same logical capability is sent under both prefixed and unprefixed keys with different values
        if (w3cCaps?.alwaysMatch) {
            const am = w3cCaps.alwaysMatch as Record<string, unknown>;
            for (const cap of ['app', 'appArguments', 'appWorkingDir', 'appTopLevelWindow']) {
                const unprefixed = am[cap];
                const prefixed = am[`appium:${cap}`];
                if (unprefixed !== undefined && prefixed !== undefined && unprefixed !== prefixed) {
                    this.log.warn(`Conflicting values for '${cap}': unprefixed='${unprefixed}', appium:${cap}='${prefixed}'. The appium:-prefixed value takes precedence.`);
                }
            }
        }

        try {
            this.log.debug('Creating AppiumWincore driver session...');
            const [sessionId, caps] = await super.createSession(jwpCaps, reqCaps, w3cCaps, driverData);
            if (caps.logFile !== undefined && caps.logFile !== false) {
                try {
                    this._logFileMirror = attachLogFileMirror(this.log as unknown as Record<string, unknown>, caps.logFile);
                    this.log.info(`Driver log is being mirrored to ${this._logFileMirror.path}`);
                } catch (e) {
                    const msg = e instanceof Error ? e.message : String(e);
                    this.log.warn(`Failed to attach log file mirror: ${msg}`);
                }
            }
            // Stamp the driver version in the log so we can tell which build is
            // running — useful when testing across rebuilds. The C# server
            // prints its own version banner shortly after via stderr → log.
            this.log.info(`appium-wincore-driver v${DRIVER_VERSION} (session ${sessionId})`);
            if (caps.smoothPointerMove) {
                assertSupportedEasingFunction(caps.smoothPointerMove);
            }
            if (caps.app && caps.appTopLevelWindow) {
                throw new errors.InvalidArgumentError('Invalid capabilities. Specify either app or appTopLevelWindow.');
            }
            if (this.caps.shouldCloseApp === undefined) {
                this.caps.shouldCloseApp = true; // set default value
            }

            if (this.caps.systemPort) {
                this.log.info(`systemPort capability (${this.caps.systemPort}) is ignored. AppiumWincoreDriver uses stdin/stdout IPC.`);
            }

            // UIA server always starts. IEDriverServer starts lazily on first IE window switch.
            {
                await this.startServerSession();

                // Java Swing (JAB) and .NET (WinForms/WPF) bridge attach are not
                // driver concerns — the appium-wincore-java-bridge and
                // appium-wincore-dotnet-bridge plugins own them via
                // `windows: attachJavaSwing` / `windows: attachDotnetBridge`,
                // called after switching to the target window (same shape as the
                // uia-bridge plugin).

                if (this.caps.prerun) {
                    this.log.info('Executing prerun PowerShell script...');
                    await this.executePowerShellScript(this.caps.prerun as Exclude<Parameters<typeof commands['executePowerShellScript']>[0], string>);
                }
            }

            this.log.debug(`Started session ${sessionId}.`);
            return [sessionId, caps];
        } catch (e) {
            await this.deleteSession();
            throw e;
        }
    }

    override async deleteSession(sessionId?: string | null | undefined): Promise<void> {
        this.log.debug('Deleting AppiumWincore driver session...');

        if (this.ieSession) {
            try {
                await this.terminateIEMode();
            } catch (e) {
                const msg = e instanceof Error ? e.message : String(e);
                this.log.warn(`Failed to terminate IE bridge during session teardown: ${msg}`);
            }
        }

        if (this.chromedriver) {
            try {
                await this.chromedriver.stop();
            } catch (e) {
                const msg = e instanceof Error ? e.message : String(e);
                this.log.warn(`Failed to stop chromedriver during session teardown: ${msg}`);
            }
            this.chromedriver = null;
            this.jwpProxyActive = false;
            this.proxyReqRes = null;
            this.proxyCommand = null;
            this.currentContext = null;
        }

        if (this.caps.shouldCloseApp && this.caps.app && this.caps.app.toLowerCase() !== 'root') {
            try {
                if (this.caps['ms:forcequit'] === true) {
                    // Force quit the process
                    const isNotNull = await this.sendCommand('checkRootElementNotNull', {}) as boolean;
                    if (isNotNull) {
                        const processId = await this.sendCommand('getProperty', { elementId: await this.sendCommand('saveRootElementToTable', {}), property: 'ProcessId' }) as string;
                        await this.sendCommand('stopProcess', { pid: Number(processId), force: true });
                    }
                } else {
                    const rootId = await this.sendCommand('saveRootElementToTable', {}) as string;
                    if (rootId) {
                        await this.sendCommand('closeWindow', { elementId: rootId });
                    }
                }
            } catch (err) {
                this.log.debug(`[deleteSession] Postrun cleanup (process stop / window close) failed: ${err instanceof Error ? err.message : err}`);
            }
        }
        if (this.caps.postrun) {
            this.log.info('Executing postrun PowerShell script...');
            await this.executePowerShellScript(this.caps.postrun as Exclude<Parameters<typeof commands['executePowerShellScript']>[0], string>);
        }

        await this.releaseActions();
        await this.terminateServerSession();

        await super.deleteSession(sessionId);

        if (this._logFileMirror) {
            this._logFileMirror.detach();
            this._logFileMirror = null;
        }
    }

    private async processSelector(strategy: string, selector: string): Promise<[string, string]> {
        if (strategy !== 'css selector') {
            return [strategy, selector];
        }

        this.log.warn('Warning: Use Appium mobile selectors instead of Selenium By, since most of them are based on CSS.');

        const nativeLocator = await cssToNativeLocator(selector);
        return [nativeLocator.strategy, nativeLocator.selector];
    }
}
