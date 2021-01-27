import datetime
name = input("what is your name: ")
age = int(input('what\'s your age: '))
now = datetime.datetime.now()
diff = now.year - age
BC = diff + 56
print(f'Hi {name}, You were born on {diff}AD and {BC}')