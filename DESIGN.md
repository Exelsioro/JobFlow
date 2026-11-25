---
name: JobFlow
description: Local job-search workspace with profile-scoped records and explicit action states.
colors:
  primary: "#225e47"
  primary-hover: "#164932"
  canvas: "#f5f5f0"
  surface: "#ffffff"
  ink: "#172923"
  muted: "#52645b"
  line: "#d3dcd4"
  navigation: "#173c32"
  navigation-active: "#d6edbe"
  overview: "#ffffff"
  danger: "#9d2929"
  error-bg: "#fbe8e5"
  error-text: "#812525"
  success-bg: "#e3efdf"
  success-text: "#214c31"
typography:
  body:
    fontFamily: '"Segoe UI", sans-serif'
    fontSize: "16px"
    fontWeight: 400
    lineHeight: 1.5
  heading:
    fontFamily: '"Segoe UI", sans-serif'
    fontSize: "32px"
    fontWeight: 700
    lineHeight: 1.2
    letterSpacing: "-0.025em"
  section:
    fontSize: "21px"
    fontWeight: 700
    lineHeight: 1.3
  label:
    fontSize: "14px"
    fontWeight: 600
  table:
    fontSize: "14px"
  supporting:
    fontSize: "13px"
rounded:
  control: "5px"
  table: "6px"
  panel: "8px"
spacing:
  small: "8px"
  control-gap: "12px"
  medium: "16px"
  group: "24px"
  large: "32px"
components:
  button-primary:
    backgroundColor: "{colors.primary}"
    textColor: "{colors.surface}"
    rounded: "{rounded.control}"
    padding: "9px 16px"
  button-primary-hover:
    backgroundColor: "{colors.primary-hover}"
  button-secondary:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.ink}"
    rounded: "{rounded.control}"
    padding: "9px 16px"
  input:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.ink}"
    rounded: "{rounded.control}"
    padding: "10px 12px"
  editor:
    backgroundColor: "{colors.surface}"
    rounded: "{rounded.panel}"
    padding: "24px"
---

# Design System: JobFlow

## Overview

**Creative North Star: "Local workbench"**

Operate mode: readable records, inline editing and explicit feedback. The interface uses a quiet green and neutral palette for a daytime desktop workspace, adapting the same controls to mobile web. This records the implemented design, not a claim that the complete product is finished.

**Key Characteristics:**

- Tables carry records; forms carry configuration.
- Primary actions remain distinct from secondary and row actions.
- Empty and unsupported states explain the available next step.

## Colors

Primary green identifies actions and selected navigation. A dark forest-green sidebar separates navigation from the light canvas and white work surfaces. Ink and muted text carry hierarchy without depending on color alone.

**The Status Truth Rule.** Success styling confirms the operation actually completed; queued work must remain described as queued. Unsupported features are labelled explicitly.

## Typography

The implemented interface uses Segoe UI with a sans-serif fallback. Page headings, section headings, body copy, labels and supporting text follow the frontmatter roles. Paragraphs are limited to 72ch; tables use compact text. Numeric summaries use tabular numerals.

Page headings reduce to 27px at the mobile breakpoint. The decision queue and recent-run headings use the 21px section role. System typography is recorded as implementation evidence, not a distinctive display-brand prescription.

## Layout

Desktop uses a 240px sidebar beside a flexible main column. The page has a maximum width of 1500px with 38px vertical and 40px horizontal padding. At 1100px, the sidebar becomes 210px and page padding becomes 28px by 24px.

At 760px and below, the sidebar returns to normal flow and a Menu button opens the grouped navigation. Choosing a page closes the mobile menu. Page padding becomes 28px by 16px, and two-column forms become one column. The application list stacks into labelled rows; wide diagnostic tables scroll within their own container.

## Elevation & Depth

No shadows are used. Tone, spacing and single borders distinguish surfaces. Editing panels remain flat and use a light outline; the overview pairs a decision queue with a compact run journal on white surfaces, stacking them at 1100px.

## Shapes

Controls use modest corners, tables and empty states use a slightly larger radius, and editing panels use the panel radius. Dividers are thin. Empty states use a dashed border rather than implying populated content.

## Components

### Buttons

Primary buttons use green with white text; secondary buttons use a white surface and outline; row actions use transparent backgrounds. Standard actions have a 44px minimum height. Navigation items use the same 44px minimum. Disabled buttons reduce opacity and use a non-interactive cursor. Hover changes the background; reduced-motion users receive no authored transition.

### Inputs

Native inputs, selects and textareas use white surfaces, a muted green-gray border and persistent labels. Inputs have a 44px minimum height. Textareas resize vertically. Checkboxes use the primary accent.

### Navigation

Desktop navigation keeps Overview, Vacancies, Applications and Notifications visible, followed by five collapsible groups: Profile and resumes, Vacancy search, Applications and replies, Services and connections, and History and logs. Group headings use chevrons and explicit expanded states; child items are indented. The selected page receives a green tint and heavier type. Its group opens when navigating directly to a page, and expanded groups are remembered in the browser. Mobile uses a Menu button above the same hierarchy. The working-profile selector sits above the content.

### Tables and forms

Tables have a pale header, left-aligned text and row dividers. Long values wrap. Editing forms use two columns, with long text fields spanning both; mobile uses one column. Errors and success feedback appear above the active content.

### Focus and browser surfaces

Keyboard focus uses a visible 3px ochre outline with a 3px offset. Selection, carets and scrollbars have explicit palette treatments. Links retain underlines with a 3px offset.

## Do's and Don'ts

- Do use API-backed records and counts.
- Do keep unsupported capabilities explicit.
- Do retain visible keyboard focus and labelled native controls.
- Do scope profile-owned records to the selected working profile.
- Don't present queued work as completed.
- Don't add fabricated users, testimonials or decorative records.
- Don't require remote fonts or trackers for the local interface.
