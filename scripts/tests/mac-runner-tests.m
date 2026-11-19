#import <Cocoa/Cocoa.h>
#import "CleaningRunner.h"
int main(void) { @autoreleasepool {
    NSString *dir = [NSTemporaryDirectory() stringByAppendingPathComponent:NSUUID.UUID.UUIDString];
    [NSFileManager.defaultManager createDirectoryAtPath:dir withIntermediateDirectories:YES attributes:nil error:nil];
    NSString *exe = [dir stringByAppendingPathComponent:@"worker"];
    NSURL *input = [NSURL fileURLWithPath:@"/tmp/Договор 'пример'.pdf"];
    // Each case: reply, exit status of the worker, expectation (0 valid success, 1 valid partial, 2 valid failure, 3 rejected).
    NSArray *outputs = @[
        @{@"schemaVersion": @2, @"inputPath": input.path, @"outputPath": @"/tmp/result.pdf", @"success": @YES, @"incomplete": @NO, @"message": @"Предупреждение", @"backupPath": @"/tmp/backups/Договор.pdf"},
        @{@"schemaVersion": @2, @"inputPath": @"wrong", @"outputPath": @"/tmp/result.pdf", @"success": @YES, @"incomplete": @NO},
        @{@"schemaVersion": @2, @"inputPath": input.path, @"outputPath": NSNull.null, @"success": @NO, @"incomplete": @NO, @"message": @"Ошибка", @"backupPath": NSNull.null},
        @{@"schemaVersion": @99, @"inputPath": input.path, @"outputPath": @"/tmp/result.pdf", @"success": @YES, @"incomplete": @NO},
        @{@"schemaVersion": @2, @"inputPath": input.path, @"outputPath": @"/tmp/result.pdf", @"success": @YES, @"incomplete": @YES, @"message": @"Остались метаданные"},
        @{@"schemaVersion": @2, @"inputPath": input.path, @"outputPath": @"/tmp/result.pdf", @"success": @YES, @"incomplete": @YES},
        @{@"schemaVersion": @1, @"inputPath": input.path, @"outputPath": @"/tmp/result.pdf", @"success": @YES},
        @{@"schemaVersion": @2, @"inputPath": input.path, @"outputPath": @"/tmp/result.pdf", @"success": @YES, @"incomplete": @NO, @"backupPath": @"relative.pdf"}
    ];
    int statuses[] = {0, 0, 2, 0, 1, 0, 0, 0, 0, 2};
    int expected[] = {0, 3, 2, 3, 1, 3, 3, 3, 3, 3};
    for (NSUInteger i=0; i<outputs.count+2; i++) {
        NSString *json = i < outputs.count ? [[NSString alloc] initWithData:[NSJSONSerialization dataWithJSONObject:outputs[i] options:0 error:nil] encoding:NSUTF8StringEncoding] : @"bad json";
        NSString *script = [NSString stringWithFormat:@"#!/bin/sh\ncat <<'RESULT'\n%@\nRESULT\nexit %d\n", json, statuses[i]];
        [script writeToFile:exe atomically:YES encoding:NSUTF8StringEncoding error:nil];
        [NSFileManager.defaultManager setAttributes:@{NSFilePosixPermissions:@0755} ofItemAtPath:exe error:nil];
        CleaningRunner *runner = [[CleaningRunner alloc] initWithExecutableURL:[NSURL fileURLWithPath:exe]];
        __block BOOL done=NO; int want=expected[i];
        [runner cleanFileURL:input backup:YES completion:^(NSDictionary *result, NSError *error) {
            if (want == 0) { NSCAssert(error == nil, @"valid reply"); NSCAssert([result[@"message"] isEqual:@"Предупреждение"] && [result[@"backupPath"] isEqual:@"/tmp/backups/Договор.pdf"], @"warning and backup preserved"); }
            else if (want == 1) { NSCAssert(error == nil && [result[@"incomplete"] boolValue], @"partial cleaning reported as partial, not success"); }
            else if (want == 2) { NSCAssert(error == nil && ![result[@"success"] boolValue], @"failure preserved"); }
            else NSCAssert(error != nil, @"invalid reply must fail (case %lu)", (unsigned long)i);
            done=YES;
        }];
        NSDate *deadline=[NSDate dateWithTimeIntervalSinceNow:10];
        while (!done && deadline.timeIntervalSinceNow > 0) [NSRunLoop.currentRunLoop runUntilDate:[NSDate dateWithTimeIntervalSinceNow:0.01]];
        NSCAssert(done, @"worker timed out");
    }
    // Closing the window stops the worker with SIGTERM; a worker that handles it reports an ordinary failure.
    NSString *failure=[[NSString alloc] initWithData:[NSJSONSerialization dataWithJSONObject:@{@"schemaVersion": @2, @"inputPath": input.path, @"outputPath": NSNull.null, @"success": @NO, @"incomplete": @NO, @"message": @"Очистка прервана."} options:0 error:nil] encoding:NSUTF8StringEncoding];
    NSString *reply=[dir stringByAppendingPathComponent:@"reply.json"];
    [failure writeToFile:reply atomically:YES encoding:NSUTF8StringEncoding error:nil];
    NSString *slow=[NSString stringWithFormat:@"#!/bin/sh\ntrap 'cat \"%@\"; exit 2' TERM\nsleep 30 & wait $!\n", reply];
    [slow writeToFile:exe atomically:YES encoding:NSUTF8StringEncoding error:nil];
    [NSFileManager.defaultManager setAttributes:@{NSFilePosixPermissions:@0755} ofItemAtPath:exe error:nil];
    CleaningRunner *runner = [[CleaningRunner alloc] initWithExecutableURL:[NSURL fileURLWithPath:exe]];
    __block BOOL stopped=NO;
    [runner cleanFileURL:input backup:NO completion:^(NSDictionary *result, NSError *error) {
        NSCAssert(error == nil && ![result[@"success"] boolValue] && [result[@"message"] isEqual:@"Очистка прервана."], @"cancelled worker reports failure");
        stopped=YES;
    }];
    [NSRunLoop.currentRunLoop runUntilDate:[NSDate dateWithTimeIntervalSinceNow:0.5]];
    [runner cancel];
    NSDate *limit=[NSDate dateWithTimeIntervalSinceNow:10];
    while (!stopped && limit.timeIntervalSinceNow > 0) [NSRunLoop.currentRunLoop runUntilDate:[NSDate dateWithTimeIntervalSinceNow:0.01]];
    NSCAssert(stopped && limit.timeIntervalSinceNow > 5, @"cancel must stop the worker promptly");
    [NSFileManager.defaultManager removeItemAtPath:dir error:nil];
    puts("Native runner: 10 cases (success, partial, failure, schema, exit status) and cancellation PASS");
} return 0; }
