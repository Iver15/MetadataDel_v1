#import "FileDropView.h"
@interface FileDropView ()
@property BOOL highlighted;
@end
@implementation FileDropView
- (instancetype)initWithFrame:(NSRect)frame {
    if ((self=[super initWithFrame:frame])) { _enabled=YES; [self registerForDraggedTypes:@[NSPasteboardTypeFileURL]]; }
    return self;
}
- (void)drawRect:(NSRect)dirtyRect {
    [super drawRect:dirtyRect];
    NSBezierPath *shape=[NSBezierPath bezierPathWithRoundedRect:NSInsetRect(self.bounds,1,1) xRadius:14 yRadius:14];
    [[NSColor.controlBackgroundColor colorWithAlphaComponent:0.7] setFill]; [shape fill];
    [(self.highlighted ? NSColor.controlAccentColor : NSColor.separatorColor) setStroke];
    CGFloat dash[]={5,4}; [shape setLineDash:dash count:2 phase:0]; shape.lineWidth=self.highlighted ? 2 : 1; [shape stroke];
}
- (NSDragOperation)draggingEntered:(id<NSDraggingInfo>)sender {
    self.highlighted=self.enabled && [sender.draggingPasteboard canReadObjectForClasses:@[NSURL.class] options:@{NSPasteboardURLReadingFileURLsOnlyKey:@YES}];
    self.needsDisplay=YES; return self.highlighted ? NSDragOperationCopy : NSDragOperationNone;
}
- (NSDragOperation)draggingUpdated:(id<NSDraggingInfo>)sender { return [self draggingEntered:sender]; }
- (void)draggingExited:(id<NSDraggingInfo>)sender { self.highlighted=NO; self.needsDisplay=YES; }
- (BOOL)performDragOperation:(id<NSDraggingInfo>)sender {
    [self draggingExited:sender]; if (!self.enabled) return NO;
    NSArray *urls=[sender.draggingPasteboard readObjectsForClasses:@[NSURL.class] options:@{NSPasteboardURLReadingFileURLsOnlyKey:@YES}];
    if (urls.count && self.onFiles) self.onFiles(urls); return urls.count>0;
}
@end
