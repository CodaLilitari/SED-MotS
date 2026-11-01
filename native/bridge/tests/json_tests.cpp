#include "json.hpp"

#include <iostream>
#include <stdexcept>
#include <string>

namespace {
void expect(bool condition, const char* message)
{
    if (!condition) throw std::runtime_error(message);
}

std::string quoted(std::string_view value)
{
    bridge::Json json;
    json.string(value);
    return json.finish();
}
}

int main()
{
    try {
        expect(quoted("\"\\\n\r\t") == "\"\\\"\\\\\\u000a\\u000d\\u0009\"\n",
               "JSON metacharacters and control bytes");
        expect(quoted(std::string("a\0b", 3)) == "\"a\\u0000b\"\n", "embedded NUL");
        expect(quoted("\xc3\xa9\xf0\x9f\x8c\x99") == "\"\xc3\xa9\xf0\x9f\x8c\x99\"\n",
               "valid multibyte UTF-8 is retained");
        expect(quoted("\xc0\xaf") == "\"\\u00c0\\u00af\"\n", "overlong UTF-8 is escaped");
        expect(quoted("\xed\xa0\x80") == "\"\\u00ed\\u00a0\\u0080\"\n", "surrogate is escaped");
        expect(quoted("\xf4\x90\x80\x80") == "\"\\u00f4\\u0090\\u0080\\u0080\"\n",
               "out-of-range Unicode is escaped");
        expect(quoted("\xe2\x82") == "\"\\u00e2\\u0082\"\n", "truncated UTF-8 is escaped");
        bridge::Json boundary(5);
        boundary.append("1234");
        expect(boundary.finish() == "1234\n", "LF is included in output budget");
        bool refused = false;
        try { boundary.append("5"); } catch (const bridge::OutputLimit&) { refused = true; }
        expect(refused, "output is refused before exceeding its budget");
        std::cout << "JSON encoding and exact output bounds passed\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
