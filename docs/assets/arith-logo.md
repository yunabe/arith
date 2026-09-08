# Arith logo

`arith-logo.png` is a 1774 × 887 PNG generated with the built-in image generation tool from a user-provided visual reference.

`arith-logo.svg` is a manually reconstructed vector version of that PNG, using regular circles, solid colors, and outlined lettering. It preserves the composition with small geometric and typographic differences, and contains no embedded raster image or font dependencies. The README displays the SVG at 440 pixels wide.

`arith-logo-dark.svg` adapts the vector logo to a `#0f0f0f` background, following a second user-provided reference: an outlined dark first circle, blue and brighter violet middle circles, a white final circle, and white connecting arrow and lettering. Both SVGs use the same canvas and geometry for consistent sizing.

The README uses `<picture>` with `prefers-color-scheme` to select the dark or light SVG on GitHub, with the light SVG as the fallback. See [GitHub's documentation](https://docs.github.com/en/get-started/writing-on-github/getting-started-with-writing-and-formatting-on-github/quickstart-for-writing-on-github).

## Generation prompt

```text
Use case: logo-brand
Asset type: high-resolution logo PNG for the Arith programming language GitHub README.
Input image: reference image only; recreate cleanly at high resolution, do not upscale its blurry pixels.
Primary request: faithfully preserve the reference composition: a thin charcoal horizontal line ending in a small right-pointing arrow, with exactly four evenly spaced equal-size circular nodes centered on the line. From left to right: solid near-black circle, solid vivid electric blue circle, solid soft violet circle, and a hollow charcoal ring with white interior. The connecting line is behind the nodes and must not show inside the hollow ring. Beneath the symbol, centered, exact text "ARITH" in bold clean geometric sans-serif uppercase with generous letter spacing.
Style: precise minimalist flat vector-like brand artwork with crisp antialiased edges and geometrically regular circles. Solid colors. No gradients, shadows, textures, extra elements or watermark.
Composition: match the attached landscape logo layout, balanced whitespace around all sides. White background. Render a high-resolution landscape canvas ideally 2048 by 1024 pixels, with the symbol and wordmark occupying most of its width, like the reference.
```
