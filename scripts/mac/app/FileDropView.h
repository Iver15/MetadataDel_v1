#import <Cocoa/Cocoa.h>
@interface FileDropView : NSView <NSDraggingDestination>
@property(nonatomic) BOOL enabled;
@property BOOL plainSurface;
@property(copy) void (^onFiles)(NSArray<NSURL *> *);
@end
