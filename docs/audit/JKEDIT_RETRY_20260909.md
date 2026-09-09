# JKEdit acquisition retry — 2026-09-09

Chrome's existing extension connection is working. This retry used ordinary Chrome pages and DuckDuckGo, plus read-only Internet Archive catalog queries. It acquired **no JKEdit binary**. No installer or legacy program was run, and no editor source was changed.

## Observed live evidence

- The [ModDB JKEdit 3.24 listing](https://www.moddb.com/downloads/jkedit-324) loaded in Chrome. It names `jkedit.zip`, 1,095,847 bytes, MD5 `133265ac1064653410ac85157e9b3689`, uploaded by LithTechGuru on April 29, 2021. These are listing metadata, not independently verified payload measurements.
- The listing's normal Download Now link opened its download modal. Chrome blocked the embedded `fmt4.dl.dbolical.com` frame with “This page has been blocked by Chrome.” No download completion was observed. The blocked frame/CDN was not retried through raw HTTP, another browser, or another execution mechanism.
- A [community JKEdit article](https://jkdf2.fandom.com/wiki/JkEdit) points to the [old JKHub tool page](https://www.jkhub.net/library/index.php/Tools:JkEdit_v3.24_full). That destination now displays the March 30, 2021 announcement explaining the datacenter fire and loss of file/image storage. It does not serve a JKEdit download. This is the old JKDF2 `jkhub.net`, distinct from Raven-game `jkhub.org`.
- The independent historical [JKEdit 2.57 mirror page](https://arundominic.tripod.com/Downloads/Winamp%20Downloads.htm), previously recorded in the collection manifest, failed with `net::ERR_NAME_NOT_RESOLVED` in Chrome. Its ZIP was not acquired.
- DuckDuckGo's initial exact-phrase search returned no matches; its normal retry without quotes yielded the ModDB, community wiki, and tutorial references. It did not establish an additional working binary mirror.

## Catalog receipts

`jkhub-wayback.json` and `jkhub-archive-catalog.json` contain empty CDX results for the requested old JKHub URL/pattern. `ia-catalog.json` records zero hits for the `jkedit` catalog query. These are bounded query results, not proof that no archived copy exists.

The previous collection's JKEdit gap remains open. In particular, this retry does not add to the number of acquired editor packages or establish JKEdit native-format compatibility.


This metadata-only report is copied from the external research directory: C:\Users\Andrei\Documents\ChatGPT\sed-editor-audit\research-followup-20260909. Referenced local payloads, source snapshots, manifests, and scripts remain there; no recovered third-party file bodies are included in this repository.
