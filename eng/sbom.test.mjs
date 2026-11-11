// Tests of eng/sbom.mjs on made-up downloads (no network):
//   node --test eng/sbom.test.mjs
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { after, describe, it } from "node:test";
import { buildSbom, conptyVersion, dotnetComponents, pathInside } from "./sbom.mjs";

const tmp = mkdtempSync(path.join(tmpdir(), "sbom-"));
after(() => rmSync(tmp, { recursive: true, force: true }));

function tree(root, files) {
  for (const [rel, text] of Object.entries(files)) {
    mkdirSync(path.dirname(path.join(root, rel)), { recursive: true });
    writeFileSync(path.join(root, rel), text);
  }
  return root;
}

const assetsFor = (rid, extraLibraries = {}) => ({
  targets: { "net10.0": {}, [`net10.0/${rid}`]: {} },
  libraries: {
    "Microsoft.DotNet.ILCompiler/10.0.14": { type: "package" },
    "Microsoft.NET.ILLink.Tasks/10.0.14": { type: "package" },
    [`runtime.${rid}.Microsoft.DotNet.ILCompiler/10.0.14`]: { type: "package" },
    "ErikTerm.Core/1.0.0": { type: "project" },
    ...extraLibraries,
  },
  project: {
    frameworks: {
      "net10.0": {
        downloadDependencies: [
          { name: `Microsoft.AspNetCore.App.Runtime.${rid}`, version: "[10.0.14, 10.0.14]" },
          { name: `Microsoft.NETCore.App.Runtime.${rid}`, version: "[10.0.14, 10.0.14]" },
          { name: `Microsoft.NETCore.App.Runtime.NativeAOT.${rid}`, version: "[10.0.14, 10.0.14]" },
          { name: `runtime.${rid}.Microsoft.DotNet.ILCompiler`, version: "[10.0.14, 10.0.14]" },
        ],
      },
    },
  },
});

const linuxContents = tree(path.join(tmp, "linux", "erikterm"), {
  erikterm: "ELF",
  "README.md": "# erikterm",
  "share/terminfo/erikterm.terminfo": "erikterm|...",
});

const winContents = tree(path.join(tmp, "win", "erikterm"), {
  "erikterm.exe": "MZ",
  "SDL3.dll": "MZ sdl",
  "freetype.dll": "MZ ft",
  "zlib1.dll": "MZ z",
  "conpty.dll": "MZ conpty",
  "OpenConsole.exe": "MZ oc",
  "licenses/sdl3.txt": "zlib",
});
const vcpkg = tree(path.join(tmp, "vcpkg", "installed"), {
  "vcpkg/info/sdl3_3.2.24_x64-windows.list": "x64-windows/\nx64-windows/bin/SDL3.dll\nx64-windows/include/SDL3/SDL.h\n",
  "vcpkg/info/freetype_2.13.3_x64-windows.list": "x64-windows/bin/freetype.dll\r\nx64-windows/lib/freetype.lib\r\n",
  "vcpkg/info/zlib_1.3.1_x64-windows.list": "x64-windows/bin/zlib1.dll\n",
  "vcpkg/info/vcpkg-cmake_2024-04-23_x64-windows.list": "x64-windows/share/vcpkg-cmake/x.cmake\n",
  "x64-windows/share/sdl3/vcpkg.spdx.json": JSON.stringify({
    packages: [
      { name: "sdl3", SPDXID: "SPDXRef-port", versionInfo: "3.2.24#1", licenseConcluded: "Zlib", homepage: "https://www.libsdl.org" },
      { name: "sdl3:x64-windows", SPDXID: "SPDXRef-binary", versionInfo: "abc" },
      { name: "libsdl-org/SDL", SPDXID: "SPDXRef-resource-1", downloadLocation: "git+https://github.com/libsdl-org/SDL@release-3.2.24" },
    ],
  }),
  "x64-windows/share/freetype/vcpkg.spdx.json": JSON.stringify({
    packages: [{ name: "freetype", SPDXID: "SPDXRef-port", versionInfo: "2.13.3", licenseConcluded: "FTL OR GPL-2.0-or-later" }],
  }),
});

const build = (overrides) =>
  buildSbom({
    asset: "erikterm-linux-x64.tar.gz",
    assetData: Buffer.from("tarball"),
    contentsDir: linuxContents,
    version: "1.2.1",
    rid: "linux-x64",
    assets: assetsFor("linux-x64"),
    sdk: "10.0.403",
    created: new Date("2026-11-10T12:00:00.123Z"),
    namespace: "fixed",
    ...overrides,
  });

const pkg = (doc, name) => doc.packages.find((p) => p.name === name);
const rel = (doc, a, type, b) => doc.relationships.some((r) => r.spdxElementId === a && r.relationshipType === type && r.relatedSpdxElement === b);
const sha = (algorithm, text) => createHash(algorithm).update(text).digest("hex");

describe("buildSbom", () => {
  it("describes the download with its hashes, version and every file", () => {
    const doc = build();
    assert.equal(doc.spdxVersion, "SPDX-2.3");
    assert.equal(doc.creationInfo.created, "2026-11-10T12:00:00Z");
    const root = doc.packages.find((p) => doc.documentDescribes.includes(p.SPDXID));
    assert.equal(root.name, "erikterm-linux-x64.tar.gz");
    assert.equal(root.versionInfo, "1.2.1");
    assert.deepEqual(root.checksums.find((c) => c.algorithm === "SHA256").checksumValue, sha("sha256", "tarball"));
    assert.equal(root.downloadLocation, "https://github.com/eriklieben/erikterm/releases/download/v1.2.1/erikterm-linux-x64.tar.gz");
    assert.deepEqual(doc.files.map((f) => f.fileName), ["./erikterm/README.md", "./erikterm/erikterm", "./erikterm/share/terminfo/erikterm.terminfo"]);
    const readme = doc.files.find((f) => f.fileName === "./erikterm/README.md");
    assert.equal(readme.checksums.find((c) => c.algorithm === "SHA1").checksumValue, sha("sha1", "# erikterm"));
    const code = sha("sha1", doc.files.map((f) => f.checksums[0].checksumValue).sort().join(""));
    assert.equal(root.packageVerificationCode.packageVerificationCodeValue, code);
    for (const f of doc.files) assert.ok(rel(doc, root.SPDXID, "CONTAINS", f.SPDXID));
    assert.ok(doc.files.every((f) => /^SPDXRef-[A-Za-z0-9.-]+$/.test(f.SPDXID)));
  });

  it("records the .NET runtime as compiled in and the compilers as build tools", () => {
    const doc = build();
    const app = pkg(doc, "erikterm");
    const aot = pkg(doc, "Microsoft.NETCore.App.Runtime.NativeAOT.linux-x64");
    const ilc = pkg(doc, "runtime.linux-x64.Microsoft.DotNet.ILCompiler");
    assert.equal(aot.versionInfo, "10.0.14");
    assert.equal(aot.externalRefs[0].referenceLocator, "pkg:nuget/Microsoft.NETCore.App.Runtime.NativeAOT.linux-x64@10.0.14");
    assert.ok(rel(doc, app.SPDXID, "STATIC_LINK", aot.SPDXID));
    assert.ok(rel(doc, app.SPDXID, "STATIC_LINK", ilc.SPDXID));
    assert.ok(rel(doc, pkg(doc, "Microsoft.DotNet.ILCompiler").SPDXID, "BUILD_TOOL_OF", app.SPDXID));
    assert.ok(rel(doc, pkg(doc, "Microsoft .NET SDK").SPDXID, "BUILD_TOOL_OF", app.SPDXID));
    // Downloaded by restore but not used by a Native AOT app: not in the SBOM.
    assert.equal(pkg(doc, "Microsoft.AspNetCore.App.Runtime.linux-x64"), undefined);
    assert.equal(pkg(doc, "ErikTerm.Core"), undefined);
  });

  it("lists a NuGet package the app references as compiled in", () => {
    const doc = build({ assets: assetsFor("linux-x64", { "Some.Library/2.0.0": { type: "package" } }) });
    assert.ok(rel(doc, pkg(doc, "erikterm").SPDXID, "STATIC_LINK", pkg(doc, "Some.Library").SPDXID));
  });

  it("lists the Linux system libraries as dependencies, not as contents", () => {
    const doc = build();
    const app = pkg(doc, "erikterm");
    const root = doc.packages[0];
    for (const name of ["SDL3", "FreeType", "glibc"]) assert.ok(rel(doc, app.SPDXID, "DEPENDS_ON", pkg(doc, name).SPDXID), name);
    for (const name of ["HarfBuzz", "fontconfig"]) assert.ok(rel(doc, pkg(doc, name).SPDXID, "OPTIONAL_DEPENDENCY_OF", app.SPDXID), name);
    for (const name of ["SDL3", "FreeType", "HarfBuzz"]) assert.ok(!rel(doc, root.SPDXID, "CONTAINS", pkg(doc, name).SPDXID), name);
  });

  it("lists each bundled Windows DLL with its vcpkg port, version and license, and the console host", () => {
    const doc = build({
      asset: "erikterm-win-x64.zip",
      contentsDir: winContents,
      rid: "win-x64",
      assets: assetsFor("win-x64"),
      vcpkg,
      triplet: "x64-windows",
      conpty: "1.25.260930003",
    });
    const root = doc.packages[0];
    const sdl = pkg(doc, "sdl3");
    assert.equal(sdl.versionInfo, "3.2.24#1");
    assert.equal(sdl.licenseDeclared, "Zlib");
    assert.equal(sdl.downloadLocation, "git+https://github.com/libsdl-org/SDL@release-3.2.24");
    assert.equal(pkg(doc, "freetype").licenseDeclared, "FTL OR GPL-2.0-or-later");
    assert.equal(pkg(doc, "zlib").licenseDeclared, "NOASSERTION"); // no vcpkg.spdx.json: not made up
    assert.equal(pkg(doc, "zlib").versionInfo, "1.3.1");
    assert.equal(pkg(doc, "vcpkg-cmake"), undefined); // installed no DLL
    const dll = doc.files.find((f) => f.fileName === "./erikterm/SDL3.dll");
    assert.ok(rel(doc, root.SPDXID, "CONTAINS", sdl.SPDXID));
    assert.ok(rel(doc, sdl.SPDXID, "CONTAINS", dll.SPDXID));
    const conpty = pkg(doc, "Microsoft.Windows.Console.ConPTY");
    assert.equal(conpty.versionInfo, "1.25.260930003");
    assert.ok(rel(doc, conpty.SPDXID, "CONTAINS", doc.files.find((f) => f.fileName === "./erikterm/OpenConsole.exe").SPDXID));
    assert.equal(pkg(doc, "SDL3"), undefined); // no "from the system" entries on Windows
  });

  it("fails on a DLL no vcpkg port installed", () => {
    const contents = tree(path.join(tmp, "win2", "erikterm"), { "erikterm.exe": "MZ", "conpty.dll": "c", "OpenConsole.exe": "o", "mystery.dll": "?" });
    assert.throws(
      () => build({ asset: "erikterm-win-x64.zip", contentsDir: contents, rid: "win-x64", assets: assetsFor("win-x64"), vcpkg, triplet: "x64-windows", conpty: "1" }),
      /mystery\.dll is in the download, but no vcpkg port installed it/,
    );
  });

  it("fails on native code in a Linux download", () => {
    const contents = tree(path.join(tmp, "linux2", "erikterm"), { erikterm: "ELF", "libSDL3.so": "ELF" });
    assert.throws(() => build({ contentsDir: contents }), /native code that no package accounts for: libSDL3\.so/);
  });

  it("adds Inno Setup to the installer's SBOM", () => {
    const options = { asset: "erikterm-win-x64-setup.exe", contentsDir: winContents, rid: "win-x64", assets: assetsFor("win-x64"), vcpkg, triplet: "x64-windows", conpty: "1" };
    assert.throws(() => build(options), /--installer-version/);
    const doc = build({ ...options, installerVersion: "6.4.3" });
    assert.equal(doc.packages[0].primaryPackagePurpose, "INSTALL");
    assert.ok(rel(doc, doc.packages[0].SPDXID, "CONTAINS", pkg(doc, "Inno Setup").SPDXID));
  });

  it("fails when the app wasn't published for the runtime", () => {
    assert.throws(() => build({ assets: assetsFor("osx-arm64") }), /no target for linux-x64/);
  });
});

describe("helpers", () => {
  it("reads the pinned ConPTY version from the publish script", () => {
    assert.equal(conptyVersion("    $conptyTag = 'v1.25.2733.0'\n    $conptyVersion = '1.25.260930003'\n"), "1.25.260930003");
    assert.throws(() => conptyVersion("nothing"), /conptyVersion/);
  });

  it("requires the Native AOT packs", () => {
    const assets = assetsFor("linux-x64");
    assets.project.frameworks["net10.0"].downloadDependencies = [];
    assert.throws(() => dotnetComponents(assets, "linux-x64"), /PublishAot/);
  });

  it("takes command line paths inside the given folder only", () => {
    const root = path.join(tmp, "checkout");
    assert.equal(pathInside(root, "out/erikterm", "contents"), path.join(root, "out", "erikterm"));
    assert.equal(pathInside(root, path.join(root, "a.zip"), "asset"), path.join(root, "a.zip"));
    assert.equal(pathInside(root, ".", "contents"), root);
    assert.throws(() => pathInside(root, "../elsewhere", "contents"), /--contents \.\.\/elsewhere is outside/);
    assert.throws(() => pathInside(root, "/etc/passwd", "asset"), /outside/);
    assert.throws(() => pathInside(root, `${root}-sibling/a.zip`, "asset"), /outside/);
  });
});
