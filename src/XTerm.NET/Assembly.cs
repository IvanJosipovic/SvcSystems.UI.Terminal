using System.Runtime.CompilerServices;

// Most of this library is tested through Terminal.Write, which is the right way round: it exercises
// the parser, the handler and the buffer together, the way a hosted program does.
//
// The graphics decoders are the exception. PngDecoder is a pure byte-array-in, pixels-out function
// with five scanline filters, five colour types and a dozen ways to be malformed, and reaching it
// through an escape sequence would test the escape sequence -- a wrong pixel would surface as a
// picture that looked odd, with nothing to say which filter or which channel was at fault.
[assembly: InternalsVisibleTo("XTerm.NET.Tests")]
[assembly: InternalsVisibleTo("SvcSystems.UI.Terminal.Tests")]
