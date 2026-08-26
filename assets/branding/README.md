# QuotaGlass brand assets

`quotaglass-logo.svg` is the source of truth for the logo. The PNG and ICO files
are generated from it with:

```powershell
./assets/branding/build-logo-assets.ps1
```

The build script uses an installed Microsoft Edge or Google Chrome browser to
rasterize the SVG, then uses Python and Pillow to create the smaller PNG files
and the multi-resolution Windows icon.

The logo uses the application palette:

- Background: `#161C23`
- Gauge and needle: `#35C46A`
- Q outline: `#8F9CAA`
