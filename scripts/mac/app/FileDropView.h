#import <Cocoa/Cocoa.h>
@interface FileDropView : NSView <NSDraggingDestination>
@property BOOL enabled;
@property(copy) void (^onFiles)(NSArray<NSURL *> *);
@end
