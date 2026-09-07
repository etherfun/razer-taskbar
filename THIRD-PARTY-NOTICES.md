# Third-party notices

This file collects the third-party works this project contains code
derived from, with their attributions and license texts — the usual
unified notices list of a larger project. The project itself is licensed
under the GNU GPLv3 or any later version (see [`LICENSE`](LICENSE)).

When new third-party code is vendored or ported into this repository,
add a section here.

---

## sanraith/razer-taskbar

* Project: <https://github.com/sanraith/razer-taskbar>
* License: **MIT**
* Copyright: Copyright (c) 2023 Soma Zsják
* Used for: the Synapse log watcher and device-selection logic, ported
  to Rust (git history, `src/watcher.rs`) and then to C#
  (`src/RazerTaskbar.Core/Services/WatcherService.cs`,
  `src/RazerTaskbar.Core/Models/DeviceModels.cs`).

```text
MIT License

Copyright (c) 2023 Soma Zsják

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## OpenRazer

* Project: <https://github.com/openrazer/openrazer>
* License: **GPL-2.0-or-later** (driver sources carry
  `SPDX-License-Identifier: GPL-2.0-or-later`; © 2015 Tim Theede,
  Terri Cain, and OpenRazer contributors)
* Used for: the USB HID vendor protocol layer
  (`src/RazerTaskbar.Core/Hid/` — the 90-byte `razer_report` layout,
  XOR CRC, and the battery/charging/serial report builders), ported
  from `driver/razercommon.h`, `driver/razercommon.c` and
  `driver/razerchromacommon.c`.
* Compliance: this project is distributed under GPLv3 or any later
  version (see [`LICENSE`](LICENSE)), which satisfies GPL-2.0-or-later
  for these portions; the full license text ships in `LICENSE`.

---

*Thanks to Soma Zsják and the OpenRazer contributors — without their
reverse-engineering work this widget would have no data source.*
