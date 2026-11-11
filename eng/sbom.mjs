// Writes the SBOM of one download (SPDX 2.3 JSON, release asset <asset>.spdx.json). No dependencies; run by
// .github/workflows/build-binaries.yml right after the download is packed, on the runner that built it.
//
//   node eng/sbom.mjs --asset erikterm-win-x64.zip --contents out/erikterm --version 1.2.1 --rid win-x64 \
//     --assets-file src/ErikTerm.App/obj/project.assets.json --sdk 10.0.403 \
//     [--vcpkg <vcpkg>/installed --triplet x64-windows] [--conpty-script scripts/publish-windows.ps1] \
//     [--installer-version 6.4.3] [--out <asset>.spdx.json]
//
// What it records, and where each fact comes from (nothing is guessed: a fact that can't be found fails the script):
// - the download itself (name, version, SHA-1 and SHA-256, the release URL) and every file in it, each with its SHA-1
//   and SHA-256 (--contents, the folder the archive or installer was made from; in the archive it's the top folder);
// - erikterm, the application (MIT), and what Native AOT compiled into its binary: the .NET runtime
//   (Microsoft.NETCore.App.Runtime.NativeAOT.<rid>, runtime.<rid>.Microsoft.DotNet.ILCompiler) and any other NuGet
//   package the app references (none today: shipped code uses no packages), with the versions restore resolved
//   (--assets-file, obj/project.assets.json of src/ErikTerm.App after `dotnet publish -r <rid>`). The compiler packages
//   and the .NET SDK (--sdk) are recorded as build tools;
// - Windows: each bundled DLL with the vcpkg port it comes from, that port's version and license (--vcpkg: the
//   installed tree, info/<port>_<version>_<triplet>.list says which port installed which DLL, share/<port>/vcpkg.spdx.json
//   has its license and source), and Microsoft's console host (conpty.dll, OpenConsole.exe; the pinned package version
//   in --conpty-script). A DLL or .exe that no port or package accounts for fails the script;
// - Linux and macOS: SDL3, FreeType (required) and HarfBuzz, fontconfig (optional) as dependencies that are NOT in the
//   download: they come from the system (Arch packages / Homebrew), so they have no version here;
// - the installer (--installer-version, the Inno Setup that made erikterm-win-x64-setup.exe): its setup and uninstall
//   program are part of the .exe.
//
// Tests: node --test eng/sbom.test.mjs
import { createHash, randomUUID } from "node:crypto";
import { existsSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs } from "node:util";

const REPO = "eriklieben/erikterm";
const NOASSERTION = "NOASSERTION";

export class SbomError extends Error {}

const hash = (algorithm, data) => createHash(algorithm).update(data).digest("hex");
const checksumsOf = (data) => [
  { algorithm: "SHA1", checksumValue: hash("sha1", data) },
  { algorithm: "SHA256", checksumValue: hash("sha256", data) },
];

/** Every file under `dir`, as paths relative to it with forward slashes, sorted. */
export function listFiles(dir, prefix = "") {
  return readdirSync(path.join(dir, prefix), { withFileTypes: true })
    .flatMap((entry) => {
      const rel = prefix ? `${prefix}/${entry.name}` : entry.name;
      if (entry.isDirectory()) return listFiles(dir, rel);
      if (entry.isFile()) return [rel];
      throw new SbomError(`${rel}: not a regular file or folder (a link?)`);
    })
    .sort();
}

const exactVersion = (range) => /^\[([^,\]]+)(?:,\s*\1)?\]$/.exec(range ?? "")?.[1];

/** The .NET packages of the app for `rid`, from obj/project.assets.json: { runtime: [...], buildTools: [...], packages: [...] }. */
export function dotnetComponents(assets, rid) {
  const target = Object.keys(assets.targets ?? {}).find((t) => t.endsWith(`/${rid}`));
  if (!target) throw new SbomError(`project.assets.json has no target for ${rid}: run dotnet publish -r ${rid} first`);
  const framework = target.split("/")[0];
  const frameworkInfo = assets.project?.frameworks?.[framework] ?? Object.values(assets.project?.frameworks ?? {})[0];
  const downloads = new Map((frameworkInfo?.downloadDependencies ?? []).map((d) => [d.name, exactVersion(d.version)]));
  const pick = (name) => {
    const version = downloads.get(name);
    if (!version) throw new SbomError(`project.assets.json doesn't download ${name} (is PublishAot on?)`);
    return { name, version };
  };
  const runtime = [pick(`Microsoft.NETCore.App.Runtime.NativeAOT.${rid}`), pick(`runtime.${rid}.Microsoft.DotNet.ILCompiler`)];

  const buildToolNames = new Set(["Microsoft.DotNet.ILCompiler", "Microsoft.NET.ILLink.Tasks"]);
  const buildTools = [];
  const packages = [];
  for (const [key, library] of Object.entries(assets.libraries ?? {})) {
    if (library.type !== "package") continue; // the ErikTerm.* projects are erikterm itself
    const [name, version] = key.split("/");
    if (name === runtime[1].name) continue; // listed with the runtime
    (buildToolNames.has(name) ? buildTools : packages).push({ name, version });
  }
  return { runtime, buildTools, packages };
}

const escapeRegExp = (text) => text.replace(/[.*+?^${}()|[\]\\]/g, String.raw`\$&`);

/** Fills in `port`'s version, license, homepage and source from the port's vcpkg.spdx.json, when it has one. */
function readPortSpdx(port, spdxFile) {
  if (!existsSync(spdxFile)) return;
  const docPackages = JSON.parse(readFileSync(spdxFile, "utf8")).packages ?? [];
  const own = docPackages.find((p) => p.SPDXID === "SPDXRef-port") ?? docPackages.find((p) => p.name === port.name);
  const source = docPackages.find((p) => (p.SPDXID ?? "").startsWith("SPDXRef-resource-"));
  if (own?.versionInfo) port.version = own.versionInfo;
  const license = [own?.licenseConcluded, own?.licenseDeclared].find((l) => l && l !== NOASSERTION);
  if (license) port.license = license;
  if (own?.homepage) port.homepage = own.homepage;
  if (source?.downloadLocation) port.downloadLocation = source.downloadLocation;
}

/** The vcpkg ports that installed DLLs, from <installed>/vcpkg/info/*.list: Map(dll file name, lower case -> port). */
export function vcpkgPorts(installed, triplet) {
  const info = path.join(installed, "vcpkg", "info");
  if (!existsSync(info)) throw new SbomError(`${info} not found: is --vcpkg the vcpkg "installed" folder?`);
  const byDll = new Map();
  const listName = new RegExp(String.raw`^(.+?)_(.+)_${escapeRegExp(triplet)}\.list$`);
  for (const list of readdirSync(info).filter((f) => f.endsWith(`_${triplet}.list`))) {
    const m = listName.exec(list);
    if (!m) continue;
    const port = { name: m[1], version: m[2], license: NOASSERTION, downloadLocation: NOASSERTION, homepage: undefined };
    readPortSpdx(port, path.join(installed, triplet, "share", port.name, "vcpkg.spdx.json"));
    for (const line of readFileSync(path.join(info, list), "utf8").split(/\r?\n/)) {
      if (line.startsWith(`${triplet}/bin/`) && line.toLowerCase().endsWith(".dll")) byDll.set(path.posix.basename(line).toLowerCase(), port);
    }
  }
  return byDll;
}

/** The pinned ConPTY package version in scripts/publish-windows.ps1. */
export function conptyVersion(script) {
  const version = /\$conptyVersion\s*=\s*'([^']+)'/.exec(script)?.[1];
  if (!version) throw new SbomError("no $conptyVersion = '...' in the publish script");
  return version;
}

// Not in the Linux and macOS downloads: erikterm loads them from the system (docs/development/README.md).
const SYSTEM_LIBRARIES = {
  linux: [
    { name: "SDL3", required: true, homepage: "https://libsdl.org", where: "the distribution (Arch: sdl3)" },
    { name: "FreeType", required: true, homepage: "https://freetype.org", where: "the distribution (Arch: freetype2)" },
    { name: "HarfBuzz", required: false, homepage: "https://harfbuzz.github.io", where: "the distribution (Arch: harfbuzz); without it there are no ligatures" },
    { name: "fontconfig", required: false, homepage: "https://www.freedesktop.org/wiki/Software/fontconfig/", where: "the distribution; font lookup with fc-match / fc-list" },
    { name: "glibc", required: true, homepage: "https://www.gnu.org/software/libc/", where: "the distribution (libc, libm)" },
  ],
  osx: [
    { name: "SDL3", required: true, homepage: "https://libsdl.org", where: "Homebrew (sdl3)" },
    { name: "FreeType", required: true, homepage: "https://freetype.org", where: "Homebrew (freetype)" },
    { name: "HarfBuzz", required: false, homepage: "https://harfbuzz.github.io", where: "Homebrew (harfbuzz); without it there are no ligatures" },
    { name: "fontconfig", required: false, homepage: "https://www.freedesktop.org/wiki/Software/fontconfig/", where: "Homebrew (fontconfig); otherwise erikterm scans the font folders itself" },
  ],
  win: [],
};

/**
 * The document being built: its packages and relationships, the SPDX ids handed out so far, and the download's files
 * (`files`, `byRel` and `fileIds` keyed by the lower-case relative path).
 */
function newDocument(contentsDir) {
  const usedIds = new Set();
  const doc = {
    root: path.basename(contentsDir),
    packages: [],
    relationships: [],
    id(kind, name) {
      const base = `SPDXRef-${kind}-${name.replace(/[^A-Za-z0-9.-]/g, "-")}`;
      let candidate = base;
      let n = 2;
      while (usedIds.has(candidate)) candidate = `${base}-${n++}`;
      usedIds.add(candidate);
      return candidate;
    },
    relate(a, type, b) {
      doc.relationships.push({ spdxElementId: a, relationshipType: type, relatedSpdxElement: b });
    },
    nugetPackage(name, pkgVersion, comment) {
      return {
        SPDXID: doc.id("Package-nuget", name),
        name,
        versionInfo: pkgVersion,
        supplier: "Organization: Microsoft Corporation",
        downloadLocation: `https://www.nuget.org/api/v2/package/${name}/${pkgVersion}`,
        filesAnalyzed: false,
        licenseConcluded: NOASSERTION,
        licenseDeclared: "MIT",
        copyrightText: NOASSERTION,
        externalRefs: [{ referenceCategory: "PACKAGE-MANAGER", referenceType: "purl", referenceLocator: `pkg:nuget/${name}@${pkgVersion}` }],
        ...(comment ? { comment } : {}),
      };
    },
    // SPDX: a package that CONTAINS files is one whose files were analyzed: it lists them (hasFiles) and carries the
    // verification code over their SHA-1s. The packages inside the download own their files this way too.
    ownFiles(pkg, rels) {
      const owned = rels.map((r) => doc.byRel.get(r.toLowerCase()));
      pkg.filesAnalyzed = true;
      pkg.hasFiles = owned.map((f) => f.SPDXID);
      pkg.packageVerificationCode = {
        packageVerificationCodeValue: hash("sha1", owned.map((f) => f.checksums[0].checksumValue).sort().join("")),
      };
      for (const f of owned) if (pkg !== doc.assetPackage) doc.relate(pkg.SPDXID, "CONTAINS", f.SPDXID);
    },
  };
  return doc;
}

/** The download itself and its files. */
function addDownload(doc, { asset, assetData, contentsDir, version }) {
  const isInstaller = asset.endsWith(".exe");
  doc.assetPackage = {
    SPDXID: doc.id("Package", asset),
    name: asset,
    versionInfo: version,
    supplier: "Person: Erik Lieben",
    downloadLocation: `https://github.com/${REPO}/releases/download/v${version}/${asset}`,
    filesAnalyzed: true,
    checksums: checksumsOf(assetData),
    licenseConcluded: NOASSERTION,
    licenseDeclared: "MIT",
    copyrightText: NOASSERTION,
    primaryPackagePurpose: isInstaller ? "INSTALL" : "ARCHIVE",
    comment: isInstaller
      ? `Installs the files listed here into the user's profile (paths relative to the install folder, ./${doc.root}/ standing for it).`
      : `The files listed here are the archive's contents, under its top folder ${doc.root}/.`,
  };
  doc.packages.push(doc.assetPackage);

  doc.files = listFiles(contentsDir).map((rel) => {
    const data = readFileSync(path.join(contentsDir, rel));
    return { rel, file: { SPDXID: doc.id("File", rel), fileName: `./${doc.root}/${rel}`, checksums: checksumsOf(data), licenseConcluded: NOASSERTION, copyrightText: NOASSERTION } };
  });
  doc.byRel = new Map(doc.files.map((f) => [f.rel.toLowerCase(), f.file]));
  doc.fileIds = new Map([...doc.byRel].map(([rel, file]) => [rel, file.SPDXID]));
  doc.ownFiles(doc.assetPackage, doc.files.map((f) => f.rel));
}

/** erikterm, what Native AOT compiled into it, and the tools that built it. */
function addApplication(doc, { contentsDir, version, rid, os, assets, sdk }) {
  const exe = os === "win" ? "erikterm.exe" : "erikterm";
  if (!doc.fileIds.has(exe)) throw new SbomError(`${exe} isn't in ${contentsDir}`);
  doc.exe = exe;
  doc.app = {
    SPDXID: doc.id("Package", "erikterm"),
    name: "erikterm",
    versionInfo: version,
    supplier: "Person: Erik Lieben",
    downloadLocation: `https://github.com/${REPO}/releases/tag/v${version}`,
    filesAnalyzed: false,
    licenseConcluded: NOASSERTION,
    licenseDeclared: "MIT",
    copyrightText: "Copyright (c) Erik Lieben",
    primaryPackagePurpose: "APPLICATION",
    externalRefs: [{ referenceCategory: "PACKAGE-MANAGER", referenceType: "purl", referenceLocator: `pkg:github/${REPO}@v${version}` }],
    comment: `A Native AOT binary for ${rid}: the .NET runtime is compiled in (statically linked), see the STATIC_LINK relationships.`,
  };
  doc.packages.push(doc.app);
  doc.relate(doc.assetPackage.SPDXID, "CONTAINS", doc.app.SPDXID);
  doc.ownFiles(doc.app, [exe]);

  const dotnet = dotnetComponents(assets, rid);
  for (const component of [...dotnet.runtime, ...dotnet.packages]) {
    const pkg = doc.nugetPackage(component.name, component.version, "Compiled into the erikterm binary by Native AOT.");
    doc.packages.push(pkg);
    doc.relate(doc.app.SPDXID, "STATIC_LINK", pkg.SPDXID);
  }
  for (const tool of dotnet.buildTools) {
    const pkg = doc.nugetPackage(tool.name, tool.version, "Build tool (Native AOT compiler, trimmer); not in the download.");
    doc.packages.push(pkg);
    doc.relate(pkg.SPDXID, "BUILD_TOOL_OF", doc.app.SPDXID);
  }
  if (sdk) {
    const pkg = {
      SPDXID: doc.id("Package", "dotnet-sdk"),
      name: "Microsoft .NET SDK",
      versionInfo: sdk,
      supplier: "Organization: Microsoft Corporation",
      downloadLocation: "https://dotnet.microsoft.com/download/dotnet",
      filesAnalyzed: false,
      licenseConcluded: NOASSERTION,
      licenseDeclared: "MIT",
      copyrightText: NOASSERTION,
      comment: "Build tool; not in the download.",
    };
    doc.packages.push(pkg);
    doc.relate(pkg.SPDXID, "BUILD_TOOL_OF", doc.app.SPDXID);
  }
}

/** The vcpkg ports the download's DLLs come from (Windows). Adds each DLL to `accounted`. */
function addVcpkgPorts(doc, { vcpkg, triplet }, accounted) {
  if (!vcpkg || !triplet) throw new SbomError("a Windows download needs --vcpkg and --triplet (its DLLs come from vcpkg)");
  const byDll = vcpkgPorts(vcpkg, triplet);
  const ports = new Map(); // port name -> { pkg, dlls }
  const isPortDll = (rel) => !rel.includes("/") && rel.toLowerCase().endsWith(".dll") && rel.toLowerCase() !== "conpty.dll";
  for (const { rel } of doc.files.filter((f) => isPortDll(f.rel))) {
    const port = byDll.get(rel.toLowerCase());
    if (!port) throw new SbomError(`${rel} is in the download, but no vcpkg port installed it: add it to the SBOM (eng/sbom.mjs)`);
    if (!ports.has(port.name)) {
      const pkg = {
        SPDXID: doc.id("Package-vcpkg", port.name),
        name: port.name,
        versionInfo: port.version,
        downloadLocation: port.downloadLocation,
        filesAnalyzed: false,
        licenseConcluded: NOASSERTION,
        licenseDeclared: port.license,
        copyrightText: NOASSERTION,
        ...(port.homepage ? { homepage: port.homepage } : {}),
        primaryPackagePurpose: "LIBRARY",
        comment: `Built with vcpkg (port ${port.name}, triplet ${triplet}); its license text is in licenses/${port.name}.txt.`,
      };
      doc.packages.push(pkg);
      ports.set(port.name, { pkg, dlls: [] });
      doc.relate(doc.assetPackage.SPDXID, "CONTAINS", pkg.SPDXID);
      doc.relate(doc.app.SPDXID, "DYNAMIC_LINK", pkg.SPDXID);
    }
    ports.get(port.name).dlls.push(rel);
    accounted.add(rel.toLowerCase());
  }
  for (const { pkg, dlls } of ports.values()) doc.ownFiles(pkg, dlls);
}

/** Microsoft's console host, conpty.dll and OpenConsole.exe (Windows). Adds both to `accounted`. */
function addConsoleHost(doc, { contentsDir, conpty }, accounted) {
  if (!conpty) throw new SbomError("a Windows download needs --conpty-script (the console host it bundles)");
  const name = "Microsoft.Windows.Console.ConPTY";
  const pkg = {
    ...doc.nugetPackage(name, conpty, "Microsoft's console host from Windows Terminal: conpty.dll and OpenConsole.exe; license in licenses/conpty.txt."),
    primaryPackagePurpose: "LIBRARY",
  };
  doc.packages.push(pkg);
  doc.relate(doc.assetPackage.SPDXID, "CONTAINS", pkg.SPDXID);
  doc.relate(doc.app.SPDXID, "DYNAMIC_LINK", pkg.SPDXID);
  const consoleHost = ["conpty.dll", "openconsole.exe"];
  for (const rel of consoleHost) {
    if (!doc.fileIds.has(rel)) throw new SbomError(`${rel} isn't in ${contentsDir}`);
    accounted.add(rel);
  }
  doc.ownFiles(pkg, consoleHost);
}

/** Bundled native code (Windows); fails on native code in the download that no package accounts for. */
function addNativeCode(doc, options) {
  const accounted = new Set([doc.exe]);
  if (options.os === "win") {
    addVcpkgPorts(doc, options, accounted);
    addConsoleHost(doc, options, accounted);
  }
  const unaccounted = doc.files.filter((f) => /\.(dll|exe|so|dylib)$/i.test(f.rel) && !accounted.has(f.rel.toLowerCase()));
  if (unaccounted.length) throw new SbomError(`native code that no package accounts for: ${unaccounted.map((f) => f.rel).join(", ")}`);
}

/** Libraries that come from the system (Linux, macOS). */
function addSystemLibraries(doc, os) {
  for (const lib of SYSTEM_LIBRARIES[os]) {
    const pkg = {
      SPDXID: doc.id("Package-system", lib.name),
      name: lib.name,
      downloadLocation: NOASSERTION,
      filesAnalyzed: false,
      licenseConcluded: NOASSERTION,
      licenseDeclared: NOASSERTION,
      copyrightText: NOASSERTION,
      homepage: lib.homepage,
      primaryPackagePurpose: "LIBRARY",
      comment: `Not in this download: loaded at run time from ${lib.where}.`,
    };
    doc.packages.push(pkg);
    if (lib.required) doc.relate(doc.app.SPDXID, "DEPENDS_ON", pkg.SPDXID);
    else doc.relate(pkg.SPDXID, "OPTIONAL_DEPENDENCY_OF", doc.app.SPDXID);
  }
}

/** The installer's own code. */
function addInstaller(doc, installerVersion) {
  if (!installerVersion) throw new SbomError("an installer needs --installer-version (the Inno Setup that made it)");
  const pkg = {
    SPDXID: doc.id("Package", "inno-setup"),
    name: "Inno Setup",
    versionInfo: installerVersion,
    supplier: "Organization: Jordan Russell's Software",
    downloadLocation: "https://jrsoftware.org/isdl.php",
    filesAnalyzed: false,
    licenseConcluded: NOASSERTION,
    licenseDeclared: NOASSERTION,
    licenseComments: "Inno Setup License, https://jrsoftware.org/files/is/license.txt",
    copyrightText: NOASSERTION,
    primaryPackagePurpose: "INSTALL",
    comment: "Its setup and uninstall program are part of the installer .exe.",
  };
  doc.packages.push(pkg);
  doc.relate(doc.assetPackage.SPDXID, "CONTAINS", pkg.SPDXID);
}

/** The SPDX 2.3 document (an object) for one download. */
export function buildSbom(options) {
  const { asset, contentsDir, version, rid, installerVersion, created = new Date(), namespace = randomUUID() } = options;
  const os = rid.split("-")[0];
  if (!(os in SYSTEM_LIBRARIES)) throw new SbomError(`unknown runtime identifier ${rid}`);
  const doc = newDocument(contentsDir);
  addDownload(doc, options);
  addApplication(doc, { ...options, os });
  addNativeCode(doc, { ...options, os });
  addSystemLibraries(doc, os);
  if (asset.endsWith(".exe")) addInstaller(doc, installerVersion);
  for (const f of doc.files) doc.relate(doc.assetPackage.SPDXID, "CONTAINS", f.file.SPDXID);

  return {
    spdxVersion: "SPDX-2.3",
    dataLicense: "CC0-1.0",
    SPDXID: "SPDXRef-DOCUMENT",
    name: `${asset} ${version}`,
    documentNamespace: `https://github.com/${REPO}/sbom/${asset}/${version}/${namespace}`,
    creationInfo: {
      created: created.toISOString().replace(/\.\d{3}Z$/, "Z"),
      creators: ["Tool: erikterm-eng-sbom.mjs", "Person: Erik Lieben"],
    },
    documentDescribes: [doc.assetPackage.SPDXID],
    packages: doc.packages,
    files: doc.files.map((f) => f.file),
    relationships: [{ spdxElementId: "SPDXRef-DOCUMENT", relationshipType: "DESCRIBES", relatedSpdxElement: doc.assetPackage.SPDXID }, ...doc.relationships],
  };
}

/**
 * A path given on the command line, resolved against the working directory, and refused unless it is inside `root`:
 * the script reads the build's own output and writes its SBOM next to it, nothing elsewhere.
 */
export function pathInside(root, value, option) {
  const base = path.resolve(root);
  const resolved = path.resolve(base, value);
  if (resolved !== base && !resolved.startsWith(base + path.sep)) throw new SbomError(`--${option} ${value} is outside ${base}`);
  return resolved;
}

function main() {
  const { values } = parseArgs({
    options: {
      asset: { type: "string" },
      contents: { type: "string" },
      version: { type: "string" },
      rid: { type: "string" },
      "assets-file": { type: "string" },
      sdk: { type: "string" },
      vcpkg: { type: "string" },
      triplet: { type: "string" },
      "conpty-script": { type: "string" },
      "installer-version": { type: "string" },
      out: { type: "string" },
    },
  });
  for (const required of ["asset", "contents", "version", "rid", "assets-file"]) {
    if (!values[required]) throw new SbomError(`--${required} is required (see the top of eng/sbom.mjs)`);
  }
  // Everything is in the checkout, except the vcpkg tree: on the Windows runner that's under VCPKG_INSTALLATION_ROOT.
  const cwd = process.cwd();
  const inCheckout = (option) => pathInside(cwd, values[option], option);
  const assetFile = inCheckout("asset");
  const conptyScript = values["conpty-script"] ? inCheckout("conpty-script") : undefined;
  const doc = buildSbom({
    asset: path.basename(assetFile),
    assetData: readFileSync(assetFile),
    contentsDir: inCheckout("contents"),
    version: values.version,
    rid: values.rid,
    assets: JSON.parse(readFileSync(inCheckout("assets-file"), "utf8")),
    sdk: values.sdk,
    vcpkg: values.vcpkg ? pathInside(process.env.VCPKG_INSTALLATION_ROOT ?? cwd, values.vcpkg, "vcpkg") : undefined,
    triplet: values.triplet,
    conpty: conptyScript ? conptyVersion(readFileSync(conptyScript, "utf8")) : undefined,
    installerVersion: values["installer-version"],
  });
  const out = values.out ? inCheckout("out") : `${assetFile}.spdx.json`;
  writeFileSync(out, `${JSON.stringify(doc, null, 2)}\n`);
  const names = doc.packages.slice(1).map((p) => (p.versionInfo ? `${p.name} ${p.versionInfo}` : p.name));
  console.log(`[sbom] ${path.relative(cwd, out)}: ${doc.files.length} file(s); ${names.join(", ")}`);
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  try {
    main();
  } catch (error) {
    if (!(error instanceof SbomError)) throw error;
    console.error(`[sbom] error: ${error.message}`);
    process.exit(1);
  }
}
