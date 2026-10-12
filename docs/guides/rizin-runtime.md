# Portable Rizin runtime

Extract the complete `rizin` folder and keep its contents together. On Windows x64, run the CLI by absolute path:

```powershell
& 'C:\Tools\rizin\bin\rizin.exe' -v
& 'C:\Tools\rizin\bin\rizin.exe' -q -N -e scr.color=0 -c iI sample.exe
```

Runtime libraries and analysis support data are included. No administrator installation or system PATH change is required.

For agent workflows, install **Rizin RE Toolkit** through [Jerry's Plugin Marketplace](https://github.com/JerryLinLinLin/jerry-plugin-marketplace#install). The plugin and its skill are distributed through the marketplace.

`bundle-manifest.json` records component versions, source revisions, and build inputs. See `THIRD-PARTY.md` and `licenses/` for component licenses and notices.
