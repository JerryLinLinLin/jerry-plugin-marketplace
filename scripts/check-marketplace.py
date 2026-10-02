"""Check marketplace contracts without installing or running plugin dependencies."""
import argparse
import json
from pathlib import Path
import re
import sys
from urllib.parse import unquote, urlsplit
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SEMVER = re.compile(r"\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?")


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def compatibility_manifest(manifest):
    result = {key: value for key, value in manifest.items() if key not in ("$schema", "extensions")}
    result.update(manifest.get("extensions", {}).get("com.openai", {}))
    return result


def check(sync=False):
    errors = []

    def require(condition, message):
        if not condition:
            errors.append(message)

    catalog = read_json(ROOT / ".agents/plugins/marketplace.json")
    names = [entry["name"] for entry in catalog["plugins"]]
    require(len(names) == len(set(names)), "Marketplace plugin names must be unique")
    folders = {path.name for path in (ROOT / "plugins").iterdir() if path.is_dir()}
    require(set(names) == folders, "Marketplace entries must match the directories under plugins/")
    documents = [ROOT / path for path in ("README.md", "CONTRIBUTING.md", "docs/build.md", "docs/releases.md")]
    readmes = [ROOT / "README.md"]

    for entry in catalog["plugins"]:
        name = entry["name"]
        require(bool(re.fullmatch(r"[a-z0-9]+(?:-[a-z0-9]+)*", name)), f"Invalid plugin ID: {name}")
        require(entry["source"] == {"source": "local", "path": f"./plugins/{name}"}, f"{name}: inconsistent catalog path")
        plugin = ROOT / "plugins" / name
        require(plugin.is_dir(), f"{name}: plugin directory is missing")
        if not plugin.is_dir():
            continue
        manifest = read_json(plugin / "plugin.json")
        require(manifest["name"] == name, f"{name}: manifest identity mismatch")
        require(bool(SEMVER.fullmatch(manifest["version"])), f"{name}: invalid semantic version")
        expected = compatibility_manifest(manifest)
        compat_path = plugin / ".codex-plugin/plugin.json"
        if sync:
            compat_path.parent.mkdir(parents=True, exist_ok=True)
            compat_path.write_text(json.dumps(expected, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        require(compat_path.is_file() and read_json(compat_path) == expected,
                f"{name}: compatibility manifest drift; run python scripts/check-marketplace.py --sync")
        extension = manifest["extensions"]["com.openai"]
        require(entry["category"] == extension["interface"]["category"], f"{name}: category mismatch")
        for field, relative in (("skills", extension["skills"]), ("logo", extension["interface"].get("logo")),
                                ("mcpServers", extension.get("mcpServers"))):
            if relative:
                target = (plugin / relative).resolve()
                require(target.is_relative_to(plugin.resolve()) and target.exists(), f"{name}: invalid {field} path")
        skills = list((plugin / extension["skills"]).glob("*/SKILL.md"))
        require(bool(skills), f"{name}: no bundled skill")
        for skill in skills:
            text = skill.read_text(encoding="utf-8-sig")
            frontmatter = re.match(r"\A---\r?\n(.*?)\r?\n---", text, re.DOTALL)
            require(frontmatter is not None, f"{skill.relative_to(ROOT)}: missing frontmatter")
            if frontmatter:
                skill_name = re.search(r"^name:\s*['\"]?([a-z0-9-]+)['\"]?\s*$", frontmatter[1], re.MULTILINE)
                require(skill_name is not None and skill_name[1] == skill.parent.name,
                        f"{skill.relative_to(ROOT)}: skill name must match its directory")
                require(bool(re.search(r"^description:\s*\S", frontmatter[1], re.MULTILINE)), f"{name}: missing skill description")
        documents.extend(skills)
        documents.append(plugin / "README.md")
        readmes.append(plugin / "README.md")
        require((plugin / "LICENSE").is_file(), f"{name}: missing license")
        runtime_path = plugin / "runtime.json"
        if runtime_path.exists():
            runtime = read_json(runtime_path)
            require(runtime["plugin"] == name, f"{name}: runtime belongs to another plugin")
            require(bool(re.fullmatch(r"[a-fA-F0-9]{64}", runtime["sha256"])), f"{name}: invalid runtime SHA-256")
            expected_url = f'{manifest["repository"]}/releases/download/{runtime["tag"]}/{runtime["asset"]}'
            require(runtime["url"] == expected_url, f"{name}: runtime URL must match its exact tag and asset")
        if (plugin / "mcp.json").exists() and (plugin / ".mcp.json").exists():
            portable = read_json(plugin / "mcp.json")["mcpServers"]
            compatible = read_json(plugin / ".mcp.json")["mcpServers"]
            normalized = {key: {field: value for field, value in server.items() if not (field == "type" and value == "stdio")}
                          for key, server in portable.items()}
            require(normalized == compatible, f"{name}: MCP configurations disagree")

    # Existing native version literals remain part of the published binary identity.
    # Catch partial version bumps without rewriting an already released executable.
    hyperv_version = read_json(ROOT / "plugins/hyper-v-control/plugin.json")["version"]
    project = ET.parse(ROOT / "src/HyperVControl/HyperVControl.csproj")
    require(project.findtext(".//Version") == hyperv_version, "Hyper-V project and plugin versions disagree")
    program = (ROOT / "src/HyperVControl/Program.cs").read_text(encoding="utf-8-sig")
    for literal in re.findall(r'(?:Hyper-V Control |Version = ")(\d+\.\d+\.\d+)', program):
        require(literal == hyperv_version, "Hyper-V CLI/protocol version disagrees with its plugin")

    for readme in readmes:
        content = readme.read_text(encoding="utf-8-sig")
        require(not re.search(r"\bv?\d+\.\d+\.\d+\b|\b20\d{2}-\d{2}-\d{2}\b|/releases/(?:tag|download)/", content),
                f"{readme.relative_to(ROOT)}: keep versions, release dates, and versioned downloads out of READMEs")

    for document in documents:
        content = document.read_text(encoding="utf-8-sig")
        for link in re.findall(r"\[[^\]\n]+\]\(([^)\n]+)\)", content):
            url = urlsplit(link.strip("<>"))
            if url.scheme or url.netloc or not url.path:
                continue
            target = (document.parent / unquote(url.path)).resolve()
            require(target.is_relative_to(ROOT) and target.exists(), f"{document.relative_to(ROOT)}: broken local link {link}")
            if document.is_relative_to(ROOT / "plugins"):
                plugin = ROOT / "plugins" / document.relative_to(ROOT / "plugins").parts[0]
                require(target.is_relative_to(plugin), f"{document.relative_to(ROOT)}: link outside plugin package; use a repository URL for {link}")

    for error in errors:
        print(f"ERROR: {error}", file=sys.stderr)
    if not errors:
        print(f"PASS: {len(names)} plugins; catalog, manifests, runtime pins, README policy, and entrypoint links")
    return bool(errors)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sync", action="store_true", help="Regenerate compatibility manifests from each canonical plugin.json before checking")
    args = parser.parse_args()
    try:
        sys.exit(check(args.sync))
    except (OSError, ValueError, KeyError, TypeError) as error:
        sys.exit(f"ERROR: {error}")
