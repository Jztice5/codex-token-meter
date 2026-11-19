---
version: alpha
name: "Codex Token Meter"
description: "A compact desktop companion showing model and token usage beside the Codex composer."
colors:
  primary: "#047968"
  primary-dark: "#5ACFB6"
  surface: "#FFFFFF"
  surface-dark: "#181818"
  ink: "#202124"
  ink-dark: "#F0F2F4"
  quiet: "#52616A"
  quiet-dark: "#AEB9C1"
  line: "#D9DFDE"
  line-dark: "#393B3E"
  track: "#DCE8E4"
  track-dark: "#303F3D"
  hover: "#EAF2EF"
  hover-dark: "#303735"
typography:
  sans:
    fontFamily: "Segoe UI, Microsoft YaHei UI"
rounded:
  sm: "6px"
  md: "10px"
  lg: "12px"
spacing:
  bar: "38px"
  detail-gap: "6px"
components:
  button: { }
  meter: { }
---

# Codex Token Meter Design System

## Overview

The floating meter shows a small green status point, a model name, an abbreviated conversation total, and a details button. It uses Simplified Chinese labels on Windows and macOS. The details switch between the active conversation and today's model totals.

`MeterPalette` in `Appearance.cs` provides the shared runtime colors. The platform window implementations own spacing and corners in device-independent pixels.

## Colors

Use Codex's light or dark appearance when it is explicitly set. Otherwise use the operating system's app appearance. The green signal means usage is available and marks the cached share of input in Windows details. Use the Windows system brushes in high-contrast mode and mark the selected view with a subtle fill.

## Typography

Segoe UI handles numeric and Latin text, and Microsoft YaHei UI handles Simplified Chinese. The compact line uses 12–13 DIPs. The disclosure emphasizes its two main figures at 22 DIPs and keeps supplemental totals at 14 DIPs.

## Layout

Keep the summary line stable as figures change. The 349-by-38 DIP strip sits above the right side of the composer when its bounds are available. The disclosure opens above the strip when space permits and keeps the strip in the same place. Both stay inside the screen working area.

## Elevation & Depth

Use the opaque panel and a single hairline to make it legible above the host. Avoid motion and heavy elevation.

## Shapes

The strip uses a 10 DIP radius, the disclosure 12 DIPs, and the button hover surface 6 DIPs.

## Components

The drag handle has a separate hit target from the details button. The tray provides keyboard access and position reset. Control plus arrow keys move the focused strip. Opening details keeps focus on its button, and Escape closes it. Missing usage stays a dash with a short reason in the disclosure. The cache mark uses cached input divided by input. The time scope sits below the daily total. Model rows sort by tokens, and the list scrolls if needed.
