# Set as Desktop & Lock

Adds one item to the Windows 11 image context menu that sets the picture as both the desktop background and the lock screen.

## Requirements

Windows 11. Administrator approval is needed once to trust the certificate.

## Install

1. Download `SetDeskLock-Setup.exe` from [Releases](https://github.com/YevhenVe/set-as-desk-lock/releases).
2. Run it. If SmartScreen appears, choose **More info**, then **Run anyway**.
3. Approve the certificate prompt, then let setup restart Explorer.

### About the certificate

The app is signed with a self-signed certificate, so setup adds that certificate to the `TrustedPeople` store of the PC. Windows then trusts any package signed with the matching private key, which only the author holds. Install it only if you trust the author. Removing the app also removes the certificate.

## Use

Right-click a `.jpg`, `.jpeg`, `.jfif`, `.png`, `.bmp`, `.webp`, `.heic`, `.tif`, or `.tiff` file and open **App extensions**, then choose **Set as Desktop & Lock**. Use **Customize menu** to move it to the main menu. The item is hidden when several files are selected.

## Remove

Run `SetDeskLock-Setup.exe` again and choose **No**.

## Build from source

Requires the Windows SDK. The .NET Framework compiler included with Windows builds the code.

| Command | Result |
| --- | --- |
| `install.cmd` | Builds, signs, and installs on this PC |
| `make-setup.cmd` | Creates `dist\SetDeskLock-Setup.exe` |
| `uninstall.cmd` | Removes the app and its local certificates |

The first build creates a signing certificate in your user certificate store. Updates must be signed with the same certificate, so back it up if you plan to keep releasing.
