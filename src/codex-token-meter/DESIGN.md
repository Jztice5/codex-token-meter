---
version: alpha
name: "Codex Token Meter"
description: "A compact desktop companion showing model and token usage beside the Codex composer."
colors:
  primary: "#047968"
  primary-dark: "#5ACFB6"
  surface: "#FCFCFB"
  surface-dark: "#202123"
  ink: "#20262A"
  ink-dark: "#F0F2F4"
  quiet: "#52616A"
  quiet-dark: "#AEB9C1"
  line: "#D9DFDE"
  line-dark: "#3E454A"
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

The accepted preview is a quiet status strip with a small green point, a model name, an abbreviated conversation total, and an explicit details action. It serves one person using Codex Desktop for Windows and uses Simplified Chinese for interface labels. The disclosure switches between the active conversation and today's model totals. The cache mark is the single graphical element in the details panel.

`MeterWindow.RefreshTheme` is the runtime source for color values and mirrors the accepted palette here. `BarHeight`, `DetailGap`, `Surface` and `ButtonTemplate` own spacing and corners in device-independent pixels.

## Colors

Use the Windows app appearance setting for the light and dark surfaces. The green signal means usage is available and marks the cached share of input. Keep unavailable measurements readable as text, use Windows system brushes for high contrast, and mark the selected view with the existing subtle action fill.

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
