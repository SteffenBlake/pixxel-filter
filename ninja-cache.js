#!/usr/bin/env node
import { PixxelNinjaParser } from 'pixxel-ninja-parser';

var parser = new PixxelNinjaParser({
    league: `Crucible`,
    priceBreakpoints: {
        "TrashTier": 0, 
        "ChaosTier": 1, 
        "ExaltTier": 15, 
        "DivineTier": 215, 
        "MirrorTier": 1000 
    },
    cachePath: `./ninja-cache`,
    outputPath: `./ninja-cache/results.json`
});
await parser.runAsync();

