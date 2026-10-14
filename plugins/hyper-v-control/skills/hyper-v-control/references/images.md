# Images: catalogs, library and deploy

`hyperv_image` turns gallery images and local disks into lab VMs; `hyperv_jobs` tracks the long-running part. Start with `hyperv_image action=list`. It reads every enabled catalog and the local library, flags problems per image, and returns a `recommended` block with a one-line `hint` and the `defaultImage` that `hyperv_vm_create` uses. Listing reads only the catalog JSON files and never touches a disk URL.

## Workflow

1. `list`, then read `defaultImage`, `recommended`, each image's `warnings`, and the `library` entries.
2. For a Windows lab VM, call `hyperv_vm_create name=<vm>` without `source`: it creates a linked clone of the default image, Microsoft's **Windows 11 dev environment** (see [Default image](#default-image)). When `defaultImage.inLibrary` is null, that first call downloads the image; confirm with the user first.
3. For another image, `deploy` a library entry, or `fetch` the catalog image once and then `deploy` it. A fetch returns a job: poll `hyperv_jobs action=wait jobId=<id> waitSeconds=30` until `status` is `succeeded`, or pass `waitSeconds` (at most 3600) to wait inline. `hyperv_vm_create imageName=<catalog image>` does both in one job. A linked clone (the default) is ready in seconds.
4. Boot it: `hyperv_power start` (or `start=true`), then the Basic console. The Ubuntu desktop images open a first-boot setup wizard (user, locale, time zone) on the console. Enhanced Session works only after that setup finishes.

For Windows images the catalogs lack, build a golden image from a Windows ISO or `register` a VHDX you already have; `HYPERV_CONTROL_DEFAULT_IMAGE` can then make it the default.

## Default image

`hyperv_vm_create` uses `source=image` by default. Without `imageName` it deploys the default image:

1. `HYPERV_CONTROL_DEFAULT_IMAGE`, when set, names a library entry or else a catalog image, by exact name (case-insensitive). Set it in the MCP client's configuration or as a user environment variable; the launcher forwards `HYPERV_CONTROL_*` variables to the elevated worker.
2. Otherwise the recommended Windows catalog image: the newest fetchable Windows image of the enabled catalogs. In Microsoft's gallery that is the **Windows 11 dev environment** (Windows 11 Enterprise evaluation, version 10.0.22621, last updated 2024-07-25).
3. When no catalog offers a Windows image (offline, or the catalogs disabled), the newest Windows image the library fetched from a catalog.

A catalog image the library already holds (same SHA-256, else same name) resolves to that entry. `list` reports the choice as `defaultImage` `{ name, catalog, inLibrary, source }`, where `source` is `HYPERV_CONTROL_DEFAULT_IMAGE`, `recommended windows catalog image`, or for the third case `newest windows library image (no catalog offers one)`; an unusable variable or a missing default shows up there as `error`.

One call does the rest:

- The image is in the library: `hyperv_vm_create` makes a linked clone within the call (seconds) and returns `{ vm, disk, parent, started, warnings }`, the same result as `deploy`.
- It must be fetched: one `hyperv_jobs` job named `quick create <vm>` runs the fetch (resolving, downloading, verifying, extracting, registering) and then creates the VM; the job's `result` is the VM. `waitSeconds` (0-3600, default 60) bounds the wait. A job still running returns its state plus `next: "hyperv_jobs action=wait jobId=<id>"`; a failed one returns `{ ok: false, error, errorType, job }`. Once fetched, the image stays in the library even when creating the VM fails, so a retry takes seconds.
- The VM name, its folder's disk path and the switch are checked before any download starts, so a long download never ends in a refusal that was knowable up front.
- A fetch of the same image that is already running (from `fetch` or another `hyperv_vm_create`) is joined, never repeated: the new job mirrors that job's progress, then creates its VM. While a quick create downloads, `fetch` of that image returns `alreadyRunning` with the quick-create job.
- Cancelling the job while it downloads deletes the partial download and extraction output, as for `fetch`.

The Windows 11 dev environment is a 23.3 GB download that extracts to a 44.8 GB VHDX: about 69 GB must be free in the library root at the peak, and 44.8 GB stays used. Confirm with the user before that first download, and point `HYPERV_CONTROL_IMAGE_ROOT` at a larger volume when the default VHD path is short of space. `hyperv_image deploy` without `imageName` also uses the default image, but only once the library holds it.

## Catalogs

A catalog is a Hyper-V Quick Create gallery JSON (`{"images":[...]}`), the same file Hyper-V Manager's Quick Create reads. `catalogs.json` starts with Microsoft's feed (`microsoft`, `https://go.microsoft.com/fwlink/?linkid=851584`). `catalogAction=add` takes a `location`, which can be an http(s) URL, a `file:` URI, or an absolute local or UNC path, plus an optional `catalog` name. The file is read and parsed before it is saved, so a broken location fails at once. `remove` deletes a custom catalog; Microsoft's built-in entry is only disabled, and adding its location again re-enables it.

Parsing follows Quick Create's own rules:

- UTF-16 or UTF-8, with or without a BOM. Trailing commas, comments and any property-name case are accepted. Microsoft's feed is UTF-16 with trailing commas.
- `config.secureBoot` may be a string or a bool; `enhancedSessionTransportType` is `HvSocket` or `VMBus`.
- `disk.hash` is `<prefix>:<64 hex digits>`. The prefix is ignored: it is always SHA-256, compared case-insensitively. An entry without a valid hash is listed but never fetched.
- A disk must resolve, after redirects, to a `.zip` containing the `.vhdx` (or `.iso`) named by `archiveRelativePath`, a `.vhdx`, or an `.iso`. A `.vhd` is rejected.
- Images whose `minHostVersion`/`maxHostVersion` exclude this host are hidden; the catalog's `hidden` count says how many.
- `requirements.diskSpace` is ignored: Microsoft's values are far too small.

To add an image Microsoft never published (Canonical's Ubuntu 24.04 Hyper-V desktop build, for example), write a one-entry catalog with its `name`, `publisher`, `version`, `lastUpdated`, `config` and `disk` (`uri`, `hash`, `archiveRelativePath`) and add it. Canonical maintains such definitions at <https://github.com/canonical/ubuntu-desktop-hyper-v>; Microsoft documents the schema at <https://learn.microsoft.com/windows-server/virtualization/hyper-v/custom-vm-gallery>. The same file can also feed Quick Create through its `GalleryLocations` registry value.

## Warnings

Warnings explain why an image is or is not a good choice. Two flags keep an image out of the recommendation: `endOfLife`, and `fetchable` when it is false. An evaluation edition or a stale image is still recommended when it is the newest of its OS family.

| Warning | Meaning |
| --- | --- |
| Evaluation edition | A Windows Enterprise evaluation: Microsoft's **Windows 11 dev environment** (`WinDev2407Eval`, version 10.0.22621, last updated 2024-07-25) and the Windows 10 MSIX packaging environment. Hyper-V Quick Create still downloads and runs it (verified with the Windows 11 dev environment on 2026-10-14). The evaluation license is time-limited: run `slmgr /xpr` inside the guest for the end date (Windows evaluations run about 90 days). Take a checkpoint after the first boot, and recreate the VM from the image when the evaluation expires. The feed publishes no end date, so the plugin never marks an evaluation expired. |
| Stale image | Last updated more than three years ago, as Microsoft's Ubuntu entries (2022) and the MSIX packaging environment are. They still work, but expect a long first update. |
| Out of standard support | Ubuntu 18.04 (May 2023) and 20.04 (May 2025). |
| No hash / invalid hash / unsupported disk type | The image cannot be verified or used, so fetch refuses it. |

Library entries keep the warnings of the catalog image they came from.

## Library

The library root is `<host default VHD path>\HyperVControl\Images`, or `HYPERV_CONTROL_IMAGE_ROOT` when it is set. It holds `library.json`, `catalogs.json`, the `cache\` download directory and the image files. The elevated worker receives `HYPERV_CONTROL_*` variables from the MCP client's process environment through the launcher bridge, so the override can be set in the client's MCP configuration or as a user environment variable; restart the client after changing it.

Each entry records `name`, `kind` (`image` or `template`), `osFamily` (`windows`, `linux` or `other`), `path`, `format` (`vhdx` or `iso`), `source` (catalog, image, URI and verified SHA-256, or the registered path), sizes, `config` (`secureBoot`, `enhancedSessionTransportType`), `registeredAt`, `readOnly`, `notes`, `warnings` and `deployments` (the clones and copies made from it). `list` adds `exists` and the current file size.

- `register` adopts a local `.vhdx` or `.iso` in place, without copying or hashing it. It checks the file signature, refuses duplicate names and files, and infers a missing `osFamily` from the name (it says so in `warnings`). Pass `secureBoot=false` for guests that do not boot with Secure Boot. A `template` is made read-only at once.
- A differencing parent must never change, so the library sets the read-only attribute on any entry used for a linked clone.
- `remove` refuses with `errorType: InUse` while any VM attaches the file (`attached`, `attachedDvd`) or any disk chain uses it as a parent (`differencingParent`). The check covers current disks, checkpoint disks and recorded clones whose files still exist. `references` lists them. Remove those VMs and child disks first. `deleteFile=true` also deletes the image file and any kept archive; without it the file stays on disk.

## Fetch

A fetch job resolves the disk URL with one 64 KB range request, which gives the final URL, the size and, for a zip, the size of the entry inside. It then checks free space and downloads into `<root>\cache\<sha256>.<ext>.partial`, hashing as it writes. It verifies the SHA-256 and the file signature, streams the `archiveRelativePath` entry to `<root>\<safe name>.vhdx`, re-checks the signature and records the entry. The archive is deleted unless `keepArchive=true`. A `.vhdx` or `.iso` download becomes the library file itself. Local and UNC disks are verified in place and extracted or copied without a cache copy. Progress `message` moves through `resolving`, `verifying the partial download`, `downloading`, `extracting`, `registering` and `done`, with `bytesDone`, `bytesTotal` and `percent`.

| Event | Outcome |
| --- | --- |
| Connection dropped or stalled for 60 s | Job fails with `DownloadInterrupted`; the partial download is kept. Fetching again resumes it with an HTTP Range request. |
| `hyperv_jobs action=cancel` | Job ends `cancelled`; the partial download and any extraction output are deleted. |
| SHA-256 mismatch | Job fails with `HashMismatch`; the download is deleted. |
| Same image already in the library | `fetch` returns `alreadyInLibrary` with the entry; nothing is downloaded. |
| Same image already being fetched | `fetch` returns `alreadyRunning` with that job (a fetch job, or a `quick create` job of `hyperv_vm_create`). |

Jobs belong to the MCP process: they appear in `hyperv_jobs list` until it exits, and an exit stops them. A partial download survives an exit, and the next fetch resumes it.

Disk space: the preflight needs the remaining download plus the extracted entry plus 1 GB, and fails with `InsufficientDiskSpace` (naming `HYPERV_CONTROL_IMAGE_ROOT`) before downloading. Microsoft's Ubuntu 22.04 entry needs about 10 GB at peak (2.5 GB zip, 6.4 GB VHDX) and leaves 6.4 GB. Canonical's 24.04 build needs about 13 GB (3.7 GB, 8.2 GB). The Windows 11 dev environment needs about 69 GB (23.3 GB zip, 44.8 GB VHDX) and leaves 44.8 GB. Point `HYPERV_CONTROL_IMAGE_ROOT` at a larger volume when the default VHD path is short of space.

## Deploy

`deploy` takes `imageName` (a library entry; omitted, the default image once the library holds it), `vmName`, `linked` (default `true`), `memoryMb` (4096), `processors` (2), `switchName`, `vmPath` (default: the host's VM path), `notes` and `start`. It refuses a VM name that already exists and refuses ISO entries, which are installation media rather than bootable disks. The new disk goes to `<vmPath>\<vmName>\Virtual Hard Disks\<vmName>.vhdx`: a differencing child of the image, or a full copy when `linked=false`. The VM is Generation 2 and joins the Default Switch, found by its fixed ID, when `switchName` is omitted; without one the adapter stays disconnected (a warning says so). Any failure removes the half-created VM and its disk.

Lab defaults, which differ from Quick Create's on purpose:

| Setting | Value |
| --- | --- |
| Memory | Static, `memoryMb` |
| Automatic checkpoints | Off (they would pile up and collide with checkpoint tools) |
| Checkpoint type | Standard, so restores include memory |
| Secure Boot | Off when the image says `secureBoot: false` (Microsoft's Ubuntu entries). Otherwise on, with `MicrosoftWindows` for Windows and `MicrosoftUEFICertificateAuthority` for Linux. An `other` OS family without a setting stays off and is reported. |
| Enhanced session transport | The image's setting; `HvSocket` for Linux by default |
| vTPM | Windows only: a local key protector and `Enable-VMTPM`, applied after the Secure Boot template because the vTPM locks it |

The result has the VM as `hyperv_vm_create` returns it (state, memory, checkpoint settings, `EnhancedSessionTransportType`, `Firmware`, `Security` with `TpmEnabled`, `createdFiles`, `nextSteps`), `disk` (`path`, `type`, `parentPath`), `parent`, `started` and `warnings` (including the image's own warnings, such as the evaluation edition). `hyperv_vm_create` with `source=image` (its default) is the same linked-clone deploy and also takes `allowDuplicateName`; it additionally fetches a catalog image first and refuses parameters that only other sources use (`vhdPath`, `parentPath`, `isoPath`, `generation`, `diskGb`, dynamic memory, `secureBoot`, `tpm`, `autoCheckpoints`, `checkpointType`). Kernel debugging needs Secure Boot off; change it with `hyperv_firmware` while the VM is Off.

## Errors

Failures use the usual `{ ok: false, error, errorType }` envelope. A `fetch` or `hyperv_vm_create` whose job fails within `waitSeconds` returns that envelope with the `job` attached. `hyperv_jobs status` and `wait` return the job itself; read its `status`, `error` and `errorType`.

| errorType | Cause |
| --- | --- |
| `NotFound` | No such catalog image, catalog, library entry or file, an HTTP 404, no default image (or `HYPERV_CONTROL_DEFAULT_IMAGE` names nothing), or `deploy` without `imageName` before the library holds the default image |
| `AmbiguousName` | The image name exists in several catalogs; pass `catalog` |
| `AlreadyExists` | A library entry, catalog, file or disk with that name or path exists |
| `UnsupportedImage` | No valid hash, an unsupported disk type, a missing archive entry or a wrong file signature |
| `HashMismatch` | The download does not match the catalog's SHA-256 |
| `DownloadInterrupted` / `DownloadFailed` | The transfer broke (resumable) or the server refused it |
| `InsufficientDiskSpace` | Not enough free space for the download and extraction |
| `InUse` | `remove` found VMs or child disks still using the file |
| `InvalidLibrary` | `library.json` or `catalogs.json` is unreadable; it is never rewritten automatically |
| `Cancelled` | The job was cancelled |
