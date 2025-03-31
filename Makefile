.PHONY: debug
debug: choir-debug c-debug laye-debug score-debug

.PHONY: publish
publish: choir c laye score

.PHONY: choir-debug
choir-debug:
	@ mkdir -p out
	dotnet build ./src/Choir.Driver -r linux-x64 -c Debug
	@ cp ./src/Choir.Driver/bin/Debug/net9.0/linux-x64/choir ./out/xchoir

.PHONY: choir
choir:
	@ mkdir -p out
	dotnet publish ./src/Choir.Driver -r linux-x64 -c Release -p:PublishAot=true
	@ cp ./src/Choir.Driver/bin/Release/net9.0/linux-x64/publish/choir ./out/choir

.PHONY: c-debug
c-debug:
	@ mkdir -p out
	dotnet build ./src/Choir.FrontEnd.C -r linux-x64 -c Debug
	@ cp ./src/Choir.FrontEnd.C/bin/Debug/net9.0/linux-x64/chc ./out/xchc

.PHONY: c
c:
	@ mkdir -p out
	dotnet publish ./src/Choir.FrontEnd.C -r linux-x64 -c Release -p:PublishAot=true
	@ cp ./src/Choir.FrontEnd.C/bin/Release/net9.0/linux-x64/publish/chc ./out/chc

.PHONY: laye-debug
laye-debug:
	@ mkdir -p out
	dotnet build ./src/Choir.FrontEnd.Laye -r linux-x64 -c Debug
	@ cp ./src/Choir.FrontEnd.Laye/bin/Debug/net9.0/linux-x64/chlaye ./out/xchlaye

.PHONY: laye
laye:
	@ mkdir -p out
	dotnet publish ./src/Choir.FrontEnd.Laye -r linux-x64 -c Release -p:PublishAot=true
	@ cp ./src/Choir.FrontEnd.Laye/bin/Release/net9.0/linux-x64/publish/chlaye ./out/chlaye

.PHONY: score-debug
score-debug:
	@ mkdir -p out
	dotnet build ./src/Choir.FrontEnd.Score -r linux-x64 -c Debug
	@ cp ./src/Choir/bin/Debug/net9.0/linux-x64/choir.dll ./out/choir.dll
	@ cp ./src/Choir.FrontEnd.Score/bin/Debug/net9.0/linux-x64/chscore.dll ./out/chscore.dll
	@ cp ./src/Choir.FrontEnd.Score/bin/Debug/net9.0/linux-x64/chscore.runtimeconfig.json ./out/chscore.runtimeconfig.json
	@ cp ./src/Choir.FrontEnd.Score/bin/Debug/net9.0/linux-x64/chscore ./out/xchscore

.PHONY: score
score:
	@ mkdir -p out
	dotnet publish ./src/Choir.FrontEnd.Score -r linux-x64 -c Release -p:PublishAot=true
	@ cp ./src/Choir.FrontEnd.Score/bin/Release/net9.0/linux-x64/publish/chscore ./out/chscore

.PHONY: clean
clean:
	rm -rf out
	rm -rf src/Choir/bin src/Choir/out
	rm -rf src/Choir.FrontEnd.C/bin src/Choir.FrontEnd.C/out
	rm -rf src/Choir.FrontEnd.Laye/bin src/Choir.FrontEnd.Laye/out
	rm -rf src/Choir.FrontEnd.Score/bin src/Choir.FrontEnd.Score/out
