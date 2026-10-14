# ADR 0004: Page editing tools (pulled forward from milestone 4)

Status: accepted by the implementer. Date: 2026-10-14.

## Context

Version 0.4.0 adds the editing tools users asked for: Organize pages (visual), watermark, page numbers and
headers, image or signature stamps, crop, flatten, remove pages and PDF to text. This records how they are built
and where they differ from the plan.

## Decisions

1. **Two engines, split by job.** Anything that only rearranges pages (organize, remove pages) goes through QPDF,
   which carries links, form fields and structure over unchanged. Anything that draws on pages (stamps, crop,
   flatten) uses PDFium's editing API inside the engine host and saves a full copy with `FPDF_SaveAsCopy`.
2. **Placement is in "visual" coordinates.** Each page's crop box and /Rotate give a matrix from the page as the
   reader sees it to PDF user space, so "bottom right" and crop margins mean what the user sees, also on rotated
   pages. Tests check this with rendered pixels.
3. **Fonts.** ASCII text uses the built-in Helvetica (nothing embedded). Other text embeds Arial from Windows,
   subset to the characters used (`FPDF_SUBSET_NEW_FONTS`). PDFium does no shaping, so scripts that need it
   (Bengali, Devanagari, Arabic joining) do not render correctly yet. Text extraction reports the space of
   embedded Arial as U+00A0; it displays as a normal space.
4. **Stamped pictures are copied once per page** (PDFium has no API to share one image XObject). Pictures are
   limited to 1200 px on the longest edge to keep files small.
5. **Editing a password-protected PDF removes the password** in the copy (PDFium cannot re-encrypt). The result
   carries a warning, and the app tells the user to use Protect again.
6. **Blank pages for Organize** come from a tiny generated PDF (`BlankPdf`) that QPDF merges like any source.
7. **Not done:** editing existing text, redaction, form filling, annotations, metadata editing, and opacity for
   picture stamps. These remain in milestones 4 and 7.
