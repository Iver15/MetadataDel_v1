#import <Foundation/Foundation.h>
#import "IntegrationManager.h"
int main(void) { @autoreleasepool {
    NSString *home=[NSTemporaryDirectory() stringByAppendingPathComponent:NSUUID.UUID.UUIDString];
    NSFileManager *fm=NSFileManager.defaultManager;
    NSString *support=[home stringByAppendingPathComponent:@"Library/Application Support/MetadataDel"];
    NSString *workflow=[home stringByAppendingPathComponent:@"Library/Services/Удалить метаданные (MetadataDel).workflow/Contents"];
    NSString *bin=[home stringByAppendingPathComponent:@".local/bin"];
    for (NSString *dir in @[support,workflow,bin]) [fm createDirectoryAtPath:dir withIntermediateDirectories:YES attributes:nil error:nil];
    NSString *exe=[bin stringByAppendingPathComponent:@"metadatadel"];
    [@"#!/bin/sh\n" writeToFile:exe atomically:YES encoding:NSUTF8StringEncoding error:nil];
    [fm setAttributes:@{NSFilePosixPermissions:@0755} ofItemAtPath:exe error:nil];
    NSCAssert(![IntegrationManager isInstalledAtHome:home], @"Missing wrapper/config must not report installed");
    [@"#!/bin/sh\n" writeToFile:[support stringByAppendingPathComponent:@"finder-action.sh"] atomically:YES encoding:NSUTF8StringEncoding error:nil];
    [fm setAttributes:@{NSFilePosixPermissions:@0755} ofItemAtPath:[support stringByAppendingPathComponent:@"finder-action.sh"] error:nil];
    [exe writeToFile:[support stringByAppendingPathComponent:@"command-path"] atomically:YES encoding:NSUTF8StringEncoding error:nil];
    [@{@"NSServices":@[@{@"NSMenuItem":@{@"default":@"Удалить метаданные (MetadataDel)"}}]} writeToFile:[workflow stringByAppendingPathComponent:@"Info.plist"] atomically:YES];
    [@{@"actions":@[]} writeToFile:[workflow stringByAppendingPathComponent:@"document.wflow"] atomically:YES];
    NSCAssert([IntegrationManager isInstalledAtHome:home], @"Complete installation must be recognized");
    [@"/missing/cli" writeToFile:[support stringByAppendingPathComponent:@"command-path"] atomically:YES encoding:NSUTF8StringEncoding error:nil];
    NSCAssert(![IntegrationManager isInstalledAtHome:home], @"Stale command path must not report installed");
    [fm removeItemAtPath:home error:nil]; puts("Integration status: 3 cases PASS");
} return 0; }
