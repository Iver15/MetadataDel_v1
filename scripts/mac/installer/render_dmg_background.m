#import <Cocoa/Cocoa.h>

// Renders the DMG window background at 1x and 2x: a heading, a drag arrow between
// the app (x=170) and Applications (x=470) icon slots, and a first-launch note.
static const CGFloat W = 640, H = 400;

static void drawText(NSString *text, CGFloat top, CGFloat size, NSFontWeight weight, NSColor *color) {
    NSMutableParagraphStyle *style = [NSMutableParagraphStyle new];
    style.alignment = NSTextAlignmentCenter;
    NSDictionary *attrs = @{NSFontAttributeName: [NSFont systemFontOfSize:size weight:weight], NSForegroundColorAttributeName: color, NSParagraphStyleAttributeName: style};
    [text drawInRect:NSMakeRect(40, H - top - size * 1.5, W - 80, size * 1.6) withAttributes:attrs];
}

static BOOL render(NSString *path, CGFloat scale) {
    NSBitmapImageRep *rep = [[NSBitmapImageRep alloc] initWithBitmapDataPlanes:NULL pixelsWide:(NSInteger)(W * scale) pixelsHigh:(NSInteger)(H * scale)
        bitsPerSample:8 samplesPerPixel:4 hasAlpha:YES isPlanar:NO colorSpaceName:NSCalibratedRGBColorSpace bytesPerRow:0 bitsPerPixel:0];
    rep.size = NSMakeSize(W, H);
    [NSGraphicsContext saveGraphicsState];
    NSGraphicsContext.currentContext = [NSGraphicsContext graphicsContextWithBitmapImageRep:rep];
    NSGraphicsContext.currentContext.imageInterpolation = NSImageInterpolationHigh;

    NSGradient *background = [[NSGradient alloc] initWithStartingColor:[NSColor colorWithSRGBRed:0.985 green:0.982 blue:0.976 alpha:1]
                                                           endingColor:[NSColor colorWithSRGBRed:0.925 green:0.922 blue:0.918 alpha:1]];
    [background drawInRect:NSMakeRect(0, 0, W, H) angle:-90];

    NSColor *ink = [NSColor colorWithSRGBRed:0.11 green:0.11 blue:0.12 alpha:1];
    NSColor *muted = [NSColor colorWithSRGBRed:0.40 green:0.40 blue:0.43 alpha:1];
    NSColor *accent = [NSColor colorWithSRGBRed:0.91 green:0.45 blue:0.17 alpha:1];
    drawText(@"Установка MetadataDel", 34, 21, NSFontWeightSemibold, ink);
    drawText(@"Перетащите значок приложения в папку «Программы»", 68, 13, NSFontWeightRegular, muted);

    CGFloat y = H - 200, from = 248, to = 392;
    NSBezierPath *shaft = [NSBezierPath bezierPath];
    [shaft moveToPoint:NSMakePoint(from, y)]; [shaft lineToPoint:NSMakePoint(to - 4, y)];
    shaft.lineWidth = 3; shaft.lineCapStyle = NSLineCapStyleRound;
    CGFloat dash[] = {1, 9}; [shaft setLineDash:dash count:2 phase:0];
    [accent setStroke]; [shaft stroke];
    NSBezierPath *head = [NSBezierPath bezierPath];
    [head moveToPoint:NSMakePoint(to - 14, y + 11)]; [head lineToPoint:NSMakePoint(to, y)]; [head lineToPoint:NSMakePoint(to - 14, y - 11)];
    head.lineWidth = 3.5; head.lineCapStyle = NSLineCapStyleRound; head.lineJoinStyle = NSLineJoinStyleRound;
    [head stroke];

    NSBezierPath *divider = [NSBezierPath bezierPath];
    [divider moveToPoint:NSMakePoint(80, 96.5)]; [divider lineToPoint:NSMakePoint(W - 80, 96.5)];
    [[NSColor colorWithSRGBRed:0 green:0 blue:0 alpha:0.08] setStroke]; divider.lineWidth = 1; [divider stroke];
    drawText(@"Затем откройте MetadataDel из «Программ» — пункт «Удалить метаданные»", 312, 12, NSFontWeightRegular, muted);
    drawText(@"появится в Finder → Быстрые действия. Пароль администратора не нужен.", 330, 12, NSFontWeightRegular, muted);

    [NSGraphicsContext restoreGraphicsState];
    NSData *png = [rep representationUsingType:NSBitmapImageFileTypePNG properties:@{}];
    return [png writeToFile:path atomically:YES];
}

int main(int argc, const char *argv[]) {
    @autoreleasepool {
        if (argc != 2) { fprintf(stderr, "usage: render_dmg_background <output-dir>\n"); return 2; }
        NSString *dir = [NSString stringWithUTF8String:argv[1]];
        [NSFileManager.defaultManager createDirectoryAtPath:dir withIntermediateDirectories:YES attributes:nil error:nil];
        if (!render([dir stringByAppendingPathComponent:@"background.png"], 1) || !render([dir stringByAppendingPathComponent:@"background@2x.png"], 2)) {
            fprintf(stderr, "failed to render DMG background\n"); return 3;
        }
    }
    return 0;
}
