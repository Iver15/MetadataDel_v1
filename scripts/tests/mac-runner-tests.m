#import <Cocoa/Cocoa.h>
#import "CleaningRunner.h"
int main(void) { @autoreleasepool {
    NSString *dir = [NSTemporaryDirectory() stringByAppendingPathComponent:NSUUID.UUID.UUIDString];
    [NSFileManager.defaultManager createDirectoryAtPath:dir withIntermediateDirectories:YES attributes:nil error:nil];
    NSString *exe = [dir stringByAppendingPathComponent:@"worker"];
    NSURL *input = [NSURL fileURLWithPath:@"/tmp/Договор 'пример'.pdf"];
    NSArray *outputs = @[
        @{@"schemaVersion": @1, @"inputPath": input.path, @"outputPath": @"/tmp/result.pdf", @"success": @YES, @"message": @"Предупреждение"},
        @{@"schemaVersion": @1, @"inputPath": @"wrong", @"outputPath": @"/tmp/result.pdf", @"success": @YES},
        @{@"schemaVersion": @1, @"inputPath": input.path, @"outputPath": NSNull.null, @"success": @NO, @"message": @"Ошибка"},
        @{@"schemaVersion": @99, @"inputPath": input.path, @"outputPath": @"/tmp/result.pdf", @"success": @YES}
    ];
    for (NSUInteger i=0; i<outputs.count+2; i++) {
        NSString *json = i < outputs.count ? [[NSString alloc] initWithData:[NSJSONSerialization dataWithJSONObject:outputs[i] options:0 error:nil] encoding:NSUTF8StringEncoding] : @"bad json";
        NSString *script = [NSString stringWithFormat:@"#!/bin/sh\ncat <<'RESULT'\n%@\nRESULT\nexit %d\n", json, i == 2 || i == 5 ? 2 : 0];
        [script writeToFile:exe atomically:YES encoding:NSUTF8StringEncoding error:nil];
        [NSFileManager.defaultManager setAttributes:@{NSFilePosixPermissions:@0755} ofItemAtPath:exe error:nil];
        CleaningRunner *runner = [[CleaningRunner alloc] initWithExecutableURL:[NSURL fileURLWithPath:exe]];
        __block BOOL done=NO;
        [runner cleanFileURL:input backup:YES completion:^(NSDictionary *result, NSError *error) {
            if (i == 0) { NSCAssert(error == nil, @"valid reply"); NSCAssert([result[@"message"] isEqual:@"Предупреждение"], @"warning preserved"); }
            else if (i == 2) { NSCAssert(![result[@"success"] boolValue], @"failure preserved"); }
            else NSCAssert(error != nil, @"invalid reply must fail");
            done=YES;
        }];
        NSDate *deadline=[NSDate dateWithTimeIntervalSinceNow:10];
        while (!done && deadline.timeIntervalSinceNow > 0) [NSRunLoop.currentRunLoop runUntilDate:[NSDate dateWithTimeIntervalSinceNow:0.01]];
        NSCAssert(done, @"worker timed out");
    }
    [NSFileManager.defaultManager removeItemAtPath:dir error:nil];
    puts("Native runner: 6 cases PASS");
} return 0; }
