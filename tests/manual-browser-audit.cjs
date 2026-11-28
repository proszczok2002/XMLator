const assert = require("node:assert/strict");
const { spawn } = require("node:child_process");
const { mkdtemp, readFile } = require("node:fs/promises");
const { join, resolve } = require("node:path");
const { setTimeout: delay } = require("node:timers/promises");
const { pathToFileURL } = require("node:url");

const [browserPath, manualOutput] = process.argv.slice(2);
const languages = ["pl", "en", "de", "ru", "hr", "fr", "es", "zh-Hans"];

async function until(callback) {
    for (let attempt = 0; attempt < 100; attempt++) {
        const result = await callback();
        if (result) return result;
        await delay(100);
    }
    throw new Error("Timed out waiting for the browser.");
}

async function audit() {
    assert(browserPath && manualOutput, "Supply Chromium executable and manual audit output directory.");
    const output = resolve(manualOutput);
    const profile = await mkdtemp(join(output, "browser-profile-"));
    const browser = spawn(browserPath, [
        "--headless", "--remote-debugging-port=0", "--user-data-dir=" + profile,
        "--no-first-run", "--no-default-browser-check", "--disable-background-networking",
        "--disable-component-update", "--disable-extensions", "about:blank"
    ], { windowsHide: true, stdio: "ignore" });
    let socket;
    try {
        const port = await until(async () => {
            const info = await readFile(join(profile, "DevToolsActivePort"), "utf8").catch(() => "");
            return Number(info.split("\n")[0]) || null;
        });
        const targets = await (await fetch("http://127.0.0.1:" + port + "/json/list")).json();
        const target = targets.find(item => item.type === "page");
        socket = new WebSocket(target.webSocketDebuggerUrl);
        const pending = new Map();
        let lastId = 0;
        socket.addEventListener("message", event => {
            const message = JSON.parse(event.data);
            const request = pending.get(message.id);
            if (!request) return;
            pending.delete(message.id);
            clearTimeout(request.timeout);
            if (message.error) request.reject(new Error(message.error.message));
            else request.resolve(message.result);
        });
        await new Promise((resolve, reject) => {
            socket.addEventListener("open", resolve, { once: true });
            socket.addEventListener("error", reject, { once: true });
        });

        const send = (method, params = {}) => new Promise((resolve, reject) => {
            const id = ++lastId;
            const timeout = setTimeout(() => {
                pending.delete(id);
                reject(new Error("Timed out calling " + method));
            }, 8000);
            pending.set(id, { resolve, reject, timeout });
            socket.send(JSON.stringify({ id, method, params }));
        });
        const evaluate = async expression => {
            const reply = await send("Runtime.evaluate", { expression, returnByValue: true });
            assert(!reply.exceptionDetails, reply.exceptionDetails?.exception.description);
            return reply.result.value;
        };
        const press = async (key, virtualKey, code = key) => {
            for (const type of ["keyDown", "keyUp"]) {
                const text = type === "keyDown" && key === "Enter" ? "\r" : undefined;
                await send("Input.dispatchKeyEvent", { type, key, code, text, windowsVirtualKeyCode: virtualKey });
            }
        };

        await send("Page.enable");
        await send("Network.enable");
        await send("Network.setBlockedURLs", { urls: ["http://*", "https://*"] });
        for (const language of languages) {
            const url = pathToFileURL(join(output, "podrecznik", language + ".html")).href;
            await send("Page.navigate", { url });
            await until(() => evaluate("location.href === " + JSON.stringify(url) +
                " && document.readyState === 'complete' && document.querySelector('.search')?.hidden === false"));
            assert.equal(await evaluate("document.documentElement.lang"), language);
            const chapterIds = await evaluate("Array.from(document.querySelectorAll('main section'), node => node.getAttribute('aria-labelledby'))");
            const links = await evaluate("Array.from(document.querySelectorAll('nav li a'), node => node.hash.slice(1))");
            assert.equal(chapterIds.length, 12);
            assert.deepEqual(links, chapterIds);
            assert.equal(chapterIds.length, new Set(chapterIds).size);
            assert(await evaluate("Array.from(document.querySelectorAll('nav a')).every(link => document.querySelector(link.hash)?.tagName === 'H2')"));
            assert.equal(await evaluate("document.querySelectorAll('details summary').length"), 7);
            const { nodes } = await send("Accessibility.getFullAXTree");
            const search = nodes.find(node => node.role?.value === "searchbox");
            assert.equal(search?.name?.value, await evaluate("document.querySelector('label[for=manual-search]').textContent"));
            assert(nodes.some(node => node.role?.value === "status"));

            await press("Tab", 9);
            assert(await evaluate("document.activeElement.classList.contains('skip')"));
            await press("Enter", 13);
            assert.equal(await evaluate("document.activeElement.id"), "main");

            await evaluate("document.getElementById('manual-search').focus()");
            await send("Input.insertText", { text: "WINRE" });
            assert.deepEqual(await evaluate("Array.from(document.querySelectorAll('main section:not([hidden])'), node => node.getAttribute('aria-labelledby'))"), ["disk"]);
            assert.equal(await evaluate("document.querySelectorAll('nav li:not([hidden])').length"), 1);
            assert(await evaluate("document.getElementById('manual-results').textContent.includes('1')"));
            await send("Input.insertText", { text: "-no-match-7f4c" });
            assert.equal(await evaluate("document.querySelectorAll('main section:not([hidden])').length"), 0);
            assert(await evaluate("document.getElementById('manual-results').textContent === document.getElementById('manual-results').dataset.empty"));
            await press("Tab", 9);
            assert.equal(await evaluate("document.activeElement.type"), "reset");
            await press(" ", 32, "Space");
            await until(async () => await evaluate("document.activeElement.id") === "manual-search");
            assert.equal(await evaluate("document.querySelectorAll('main section:not([hidden])').length"), chapterIds.length);

            await evaluate("document.querySelector('summary').focus()");
            await press("Enter", 13);
            assert(await evaluate("document.querySelector('details').open"));
            await send("Emulation.setDeviceMetricsOverride", { width: 320, height: 720, deviceScaleFactor: 1, mobile: false });
            assert(await evaluate("document.documentElement.scrollWidth <= 320"));
            await send("Emulation.clearDeviceMetricsOverride");
            await send("Emulation.setEmulatedMedia", { features: [{ name: "prefers-color-scheme", value: "dark" }] });
            assert.equal(await evaluate("getComputedStyle(document.body).backgroundColor"), "rgb(16, 24, 40)");
            await send("Emulation.setEmulatedMedia", { features: [] });
            console.log(language + ": offline, 12 chapters, localized accessible search, keyboard, examples, 320px and dark colors.");
        }
    } finally {
        socket?.close();
        browser.kill();
    }
}

audit().catch(error => {
    process.stderr.write(error.stack + "\n");
    process.exitCode = 1;
});
